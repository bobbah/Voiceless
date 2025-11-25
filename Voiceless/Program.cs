using System.ClientModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Exceptions;
using DSharpPlus.VoiceNext;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using Voiceless.Configuration;
using Voiceless.Data;
using Voiceless.Voice;
using DiscordConfiguration = Voiceless.Configuration.DiscordConfiguration;

namespace Voiceless;

public static partial class Program
{
    [GeneratedRegex(@"\b(?:https?://)?(?:www\.)?[a-zA-Z0-9-]+(?:\.[a-zA-Z]{2,})+(?:/[^\s]*)?\b", RegexOptions.Multiline,
        "en-US")]
    private static partial Regex UrlPattern();

    [GeneratedRegex("<a?:(?<text>.+):[0-9]+>", RegexOptions.Multiline, "en-US")]
    private static partial Regex EmojiPattern();

    private static OpenAIClient _openAi = null!;
    private static IVoiceSynthesizer _voiceSynth = null!;
    private static IConfiguration _configuration = null!;

    private static readonly ConcurrentDictionary<ulong, ConcurrentQueue<QueuedMessage>> MessageQueues = new();
    private static HashSet<ulong> _personsOfInterest = [];
    private static HashSet<ulong> _channelsOfInterest = [];
    private static HashSet<ulong> _serversOfInterest = [];
    private static ConcurrentDictionary<ulong, string> _voices = [];
    private static ConcurrentDictionary<ulong, HashSet<ulong>> _channelsForUser = [];

    public static async Task Main()
    {
        // Get config
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true, reloadOnChange: true);
        _configuration = builder.Build();

        // Grab configuration objects
        var discordConfig = GetConfiguration<DiscordConfiguration>("discord");
        var openAIConfig = GetConfiguration<OpenAIConfiguration>("openai");

        _openAi = new OpenAIClient(new ApiKeyCredential(openAIConfig?.Token ??
                                                        throw new InvalidOperationException(
                                                            "Invalid configuration, missing OpenAI API token")),
            new OpenAIClientOptions());

        // Perform target setup
        var targetConfig = GetConfiguration<TargetConfiguration>("target");
        _personsOfInterest = [..targetConfig.Users.Select(x => x.User).Distinct()];
        _channelsOfInterest = [..targetConfig.Users.SelectMany(x => x.Servers).SelectMany(x => x.Channels).Distinct()];
        _serversOfInterest = [..targetConfig.Users.SelectMany(x => x.Servers).Select(x => x.Server).Distinct()];
        _voices = new ConcurrentDictionary<ulong, string>(targetConfig.Users.Select(x =>
            new KeyValuePair<ulong, string>(x.User, x.Voice)));
        _channelsForUser = new ConcurrentDictionary<ulong, HashSet<ulong>>(targetConfig.Users.Select(x =>
            new KeyValuePair<ulong, HashSet<ulong>>(x.User, x.Servers.SelectMany(y => y.Channels).ToHashSet())));
        foreach (var server in _serversOfInterest)
        {
            MessageQueues[server] = new ConcurrentQueue<QueuedMessage>();
        }

        // Use ElevenLabs when available
        var elevenLabsConfig = GetConfiguration<ElevenLabsConfiguration>("elevenlabs");
        if (!string.IsNullOrEmpty(elevenLabsConfig?.Token))
        {
            var synth = new ElevenLabsVoiceSynthesizer(elevenLabsConfig);
            await synth.ConfigureClient();
            _voiceSynth = synth;
        }
        else
            _voiceSynth = new OpenAIVoiceSynthesizer(_openAi, openAIConfig);

        var clientBuilder = DiscordClientBuilder.CreateDefault(discordConfig?.Token ??
                                                               throw new InvalidOperationException(
                                                                   "Invalid configuration, missing Discord bot token"),
            DiscordIntents.AllUnprivileged | DiscordIntents.MessageContents |
            DiscordIntents.GuildVoiceStates | DiscordIntents.GuildMembers);

        clientBuilder.UseVoiceNext(new VoiceNextConfiguration());

        clientBuilder.ConfigureEventHandlers(e =>
        {
            e.HandleVoiceStateUpdated(VoiceStateUpdated);
            e.HandleMessageCreated(MessageCreated);
            e.HandleSessionCreated(SessionCreated);
            e.HandleGuildMemberUpdated(GuildMemberUpdated);
        });

        var discord = clientBuilder.Build();
        await discord.ConnectAsync();
        await Task.Delay(Timeout.Infinite);
    }

    private static async Task SessionCreated(DiscordClient sender, SessionCreatedEventArgs args)
    {
        var targetConfig = GetConfiguration<TargetConfiguration>("target");

        // Update nickname in target servers
        // Create a sort of 'inverted' version of the config as we'll do this on a server-by-server basis
        var servers = new Dictionary<ulong, HashSet<ulong>>();
        foreach (var user in targetConfig.Users)
        {
            foreach (var server in user.Servers)
            {
                if (!servers.ContainsKey(server.Server))
                    servers[server.Server] = [];
                servers[server.Server].Add(user.User);
            }
        }

        foreach (var server in servers)
        {
            DiscordGuild foundServer;
            try
            {
                foundServer = await sender.GetGuildAsync(server.Key);
            }
            catch (NotFoundException)
            {
                continue;
            }

            // Get target channel, if any, if none is found don't go further
            await SetNickname(sender, foundServer);
            var targetChannel = await GetTargetChannel(sender, foundServer);
            if (targetChannel is null)
                continue;

            // Connect to target channel
            await targetChannel.ConnectAsync();
        }
    }

    private static async Task MessageCreated(DiscordClient sender, MessageCreatedEventArgs args)
    {
        if (!_personsOfInterest.Contains(args.Author.Id) || !_serversOfInterest.Contains(args.Guild.Id))
            return;
        
        var rawMessage = args.Message.Content;
        
        // Check if this is to check for listened channels
        if (rawMessage.Equals(".listening", StringComparison.InvariantCultureIgnoreCase))
        {
            var listenedChannels = GetConfiguration<TargetConfiguration>("target").Users
                .First(x => x.User == args.Author.Id).Servers.First(x => x.Server == args.Guild.Id).Channels;
            await args.Channel.SendMessageAsync(
                $"I'm currently listening to the following channels: {string.Join(",", listenedChannels.Select(x => $"<#{x}>"))}");
            return;
        }

        // Outside of anything above, ignore any non-listened channel
        if (!_channelsForUser[args.Author.Id].Contains(args.Channel.Id))
            return;

        // Determine if this message should be skipped based on configured skip prefix or current state
        var miscConfig = GetConfiguration<MiscConfiguration>("misc");
        if (rawMessage.StartsWith(miscConfig.Silencer))
            return;
        
        var vcChannel = sender.ServiceProvider.GetRequiredService<VoiceNextExtension>().GetConnection(args.Guild);
        if (vcChannel == null)
            return;

        // If the user is not in the vc channel, or not undeafened, ignore them
        if (!vcChannel.TargetChannel.Users.Any(x =>
                x.Id == args.Author.Id && x.VoiceState is { IsSelfDeafened: false, IsServerDeafened: false }))
            return;
        
        // Handle mentioned users
        foreach (var mention in args.Message.MentionedUsers)
        {
            rawMessage = rawMessage.Replace($"<@{mention.Id}>",
                $" at {(await args.Guild.GetMemberAsync(mention.Id)).DisplayName}");
        }

        // Handle mentioned channels
        foreach (var mention in args.Message.MentionedChannels)
        {
            rawMessage = rawMessage.Replace($"<#{mention.Id}>",
                $" at {(await args.Guild.GetChannelAsync(mention.Id)).Name}");
        }

        // Handle mentioned roles
        foreach (var mention in args.Message.MentionedRoles)
        {
            rawMessage = rawMessage.Replace($"<@&{mention.Id}>",
                $" at {(await args.Guild.GetRoleAsync(mention.Id)).Name}");
        }

        // Strip out emojis
        rawMessage = EmojiPattern().Replace(rawMessage, "");

        // Strip out URLs
        rawMessage = UrlPattern().Replace(rawMessage, "").Trim();

        var openAIConfig = GetConfiguration<OpenAIConfiguration>("openai");

        // Check for attachments to describe
        var imageAttachments = args.Message.Attachments.Where(x => x.MediaType != null && x.MediaType.Contains("image"))
            .ToList();
        if (imageAttachments.Count != 0)
            rawMessage += (string.IsNullOrWhiteSpace(rawMessage) ? string.Empty : ". ") +
                          (openAIConfig.DescribeAttachments
                              ? await DescribeAttachments(imageAttachments)
                              : $"I've attached {imageAttachments.Count} images to my post.");

        // Abort here if it's a basically blank message
        if (string.IsNullOrWhiteSpace(rawMessage))
            return;

        // Apply flavor prompt if it is provided
        var flavorPrompt = openAIConfig.FlavorPrompt;
        if (flavorPrompt != string.Empty)
            rawMessage = await ApplyFlavorPrompt(rawMessage);

        // Perform TTS conversion
        var audioResult = await _voiceSynth.SynthesizeTextToSpeechAsync(rawMessage, _voices[args.Author.Id]);
        if (audioResult == null)
            return;

        var guildId = vcChannel.TargetChannel.Guild.Id;
        MessageQueues[guildId].Enqueue(new QueuedMessage(audioResult, vcChannel, _voiceSynth.AudioFormat));
        if (MessageQueues[guildId].Count == 1)
            await ProcessMessageQueue(guildId);
    }

    private static async Task<string> ApplyFlavorPrompt(string rawMessage)
    {
        var openAIConfig = GetConfiguration<OpenAIConfiguration>("openai");

        var completionResult = await _openAi.GetChatClient("gpt-4o-mini").CompleteChatAsync(new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(openAIConfig.FlavorPrompt),
            ChatMessage.CreateUserMessage(rawMessage)
        });

        return completionResult != null
            ? completionResult.Value.Content.First().Text ?? "Flavor prompt application returned a null response"
            : "Failed to apply flavor prompt.";
    }

    private static async Task ProcessMessageQueue(ulong guildId)
    {
        while (MessageQueues[guildId].TryPeek(out var task))
        {
            // Send it out!
            var transmit = task.VoiceChannel.GetTransmitSink();
            await ConvertAudioToPcm(task.Stream, transmit, task.AudioFormat);
            task.Stream.Close();

            // Get rid of the item we were processing afterwards
            MessageQueues[guildId].TryDequeue(out _);
        }
    }

    private static async Task<string> DescribeAttachments(IEnumerable<DiscordAttachment> attachments)
    {
        var openAIConfig = GetConfiguration<OpenAIConfiguration>("openai");

        var completionResult = await _openAi.GetChatClient("gpt-4o-mini").CompleteChatAsync(new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(openAIConfig.AttachmentPrompt),
            ChatMessage.CreateUserMessage(attachments.Select(x =>
                    ChatMessageContentPart.CreateImagePart(new Uri(x.Url!), openAIConfig.AttachmentDetail))
                .ToList())
        }, new ChatCompletionOptions()
        {
            MaxOutputTokenCount = openAIConfig.MaxAttachmentTokens
        });

        return completionResult != null
            ? completionResult.Value.Content.First().Text ?? "Attachment analysis API returned a null response"
            : "Failed to describe attached images.";
    }

    private static async Task VoiceStateUpdated(DiscordClient sender, VoiceStateUpdatedEventArgs args)
    {
        if (!_personsOfInterest.Contains(args.UserId) || !_serversOfInterest.Contains(args.GuildId.Value))
            return;

        // Set nickname if an update is needed
        var guild = await args.GetGuildAsync();
        await SetNickname(sender, guild);

        var existingConnection = sender.ServiceProvider.GetRequiredService<VoiceNextExtension>()
            .GetConnection(guild);
        var target = await GetTargetChannel(sender, guild);
        if (target is null)
        {
            existingConnection?.Disconnect();
            return;
        }

        // If we're already in a channel don't change anything
        if (existingConnection?.TargetChannel.Id == target.Id)
            return;

        // Disconnect from any previous channel and connect to the new target
        existingConnection?.Disconnect();
        await target.ConnectAsync();
    }

    private static async Task<DiscordChannel?> GetTargetChannel(DiscordClient client, DiscordGuild guild)
    {
        var users = UsersOfInterestForServer(guild.Id).ToHashSet();
        var channels = (await guild.GetChannelsAsync())
            .Where(x => x.Type == DiscordChannelType.Voice && x.Users.Any(y => users.Contains(y.Id)))
            .Select(x => new
            {
                value = x,
                score = x.Users.Count(y =>
                    users.Contains(y.Id) && y.VoiceState is { IsSelfDeafened: false, IsServerDeafened: false })
            })
            .OrderByDescending(x => x.score)
            .ToList();

        var existingConnection = client.ServiceProvider.GetRequiredService<VoiceNextExtension>()
            .GetConnection(guild);

        // No target channels
        if (channels.Count == 0 || channels[0].score == 0)
        {
            return null;
        }

        // Only take the highest score channel[s]
        channels = channels.Where(x => x.score == channels[0].score).ToList();
        if (existingConnection != null && channels.Any(y => y.value.Id == existingConnection.TargetChannel.Id))
        {
            // We're already in the desired channel, don't bother doing anything
            return existingConnection.TargetChannel;
        }

        // Just return the first entry as we have no other tie breaker at the moment
        return channels[0].value;
    }

    private static async Task GuildMemberUpdated(DiscordClient sender, GuildMemberUpdatedEventArgs args)
    {
        if (!_personsOfInterest.Contains(args.Member.Id) || !_serversOfInterest.Contains(args.Guild.Id))
            return;

        // Don't bother processing anything other than a nickname/name change
        if (args.NicknameAfter.Equals(args.NicknameBefore))
            return;

        // Get target user in this guild
        await SetNickname(sender, args.Guild);
    }

    private static async Task SetNickname(DiscordClient client, DiscordGuild guild)
    {
        // Get target channel, if any, if none is found don't go further
        var voiceless = await guild.GetMemberAsync(client.CurrentUser.Id);
        var targetChannel = await GetTargetChannel(client, guild);
        if (targetChannel is null)
        {
            await voiceless.ModifyAsync(x => x.Nickname = "Voiceless");
            return;
        }

        // Get target user in this channel and set nickname accordingly
        var users = UsersOfInterestForServer(guild.Id);
        var targetUsers = targetChannel.Users.Where(x =>
            users.Contains(x.Id) && x.VoiceState is { IsSelfDeafened: false, IsServerDeafened: false }).ToList();

        switch (targetUsers.Count)
        {
            case 0:
                await voiceless.ModifyAsync(x => x.Nickname = "Voiceless");
                break;
            case 1:
                await voiceless.ModifyAsync(x =>
                    x.Nickname =
                        $"{targetUsers[0].DisplayName[..Math.Min(26, targetUsers[0].DisplayName.Length)]}'s Mic");
                break;
            default:
                await voiceless.ModifyAsync(x => x.Nickname = "Multi-User Mic");
                break;
        }
    }

    private static IEnumerable<ulong> UsersOfInterestForServer(ulong server) =>
        GetConfiguration<TargetConfiguration>("target")
            .Users.Where(x => x.Servers.Any(y => y.Server == server)).Select(x => x.User);

    private static async Task ConvertAudioToPcm(Stream inputStream, VoiceTransmitSink outputStream, string audioFormat)
    {
        var ffmpeg = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-f {audioFormat} -i pipe:0 -ac 2 -f s16le -ar 48000 pipe:1",
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        });

        if (ffmpeg is null)
            throw new NullReferenceException("Failed to create FFMPEG instance!");

        // Have to do input and output at the same time otherwise output buffer fills and waits
        var inputTask = Task.Run(async () =>
        {
            // Copy in input
            inputStream.Seek(0, SeekOrigin.Begin);
            await inputStream.CopyToAsync(ffmpeg.StandardInput.BaseStream);
            ffmpeg.StandardInput.Close();
        });

        var outputTask = Task.Run(async () =>
        {
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(outputStream);
            ffmpeg.StandardOutput.Close();
        });

        await Task.WhenAll(inputTask, outputTask);

        // Let conversion happen
        await ffmpeg.WaitForExitAsync();
    }

    private static T GetConfiguration<T>(string section) where T : class, new()
    {
        var toReturn = new T();
        var options = new ConfigureOptions<T>(o => _configuration.GetSection(section).Bind(o));
        options.Configure(toReturn);
        return toReturn;
    }
}