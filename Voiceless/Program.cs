using System.ClientModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;
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

    [GeneratedRegex("<#([0-9]+)>", RegexOptions.Multiline, "en-US")]
    private static partial Regex ChannelMentionPattern();

    private static OpenAIClient _openAi = null!;
    private static IVoiceSynthesizer _voiceSynth = null!;
    private static IConfiguration _configuration = null!;
    private static GatewayClient _discord = null!;
    private static RestClient _restClient = null!;

    private static readonly ConcurrentDictionary<ulong, ConcurrentQueue<QueuedMessage>> MessageQueues = new();
    private static readonly ConcurrentDictionary<ulong, VoiceClient> VoiceConnections = new();
    private static readonly ConcurrentDictionary<ulong, ulong> VoiceChannelIds = new();
    private static readonly SemaphoreSlim VoiceConnectionLock = new(1, 1);
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

        // Create the REST client for API operations
        _restClient = new RestClient(new BotToken(discordConfig?.Token ??
                                                   throw new InvalidOperationException(
                                                       "Invalid configuration, missing Discord bot token")));

        // Create and configure the Gateway client
        _discord = new GatewayClient(new BotToken(discordConfig.Token), new GatewayClientConfiguration
        {
            Intents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent |
                      GatewayIntents.GuildVoiceStates | GatewayIntents.GuildUsers
        });

        // Subscribe to events
        _discord.Ready += OnReady;
        _discord.MessageCreate += OnMessageCreate;
        _discord.VoiceStateUpdate += OnVoiceStateUpdate;
        _discord.GuildUserUpdate += OnGuildMemberUpdate;

        // Connect and run
        await _discord.StartAsync();
        await Task.Delay(Timeout.Infinite);
    }

    private static async ValueTask OnReady(ReadyEventArgs args)
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
            if (!_discord.Cache.Guilds.TryGetValue(server.Key, out var foundServer))
                continue;

            // Get target channel, if any, if none is found don't go further
            await SetNickname(foundServer);
            var targetChannel = await GetTargetChannel(foundServer);
            if (targetChannel is null)
                continue;

            // Connect to target channel
            await ConnectToVoiceChannel(foundServer.Id, targetChannel.Id);
        }
    }

    private static async ValueTask OnMessageCreate(Message message)
    {
        // Get guild ID - ignore DMs
        if (message.GuildId is null)
            return;
        
        var guildId = message.GuildId.Value;
        
        if (!_personsOfInterest.Contains(message.Author.Id) || !_serversOfInterest.Contains(guildId))
            return;
        
        var rawMessage = message.Content;
        
        // Check if this is to check for listened channels
        if (rawMessage.Equals(".listening", StringComparison.InvariantCultureIgnoreCase))
        {
            var listenedChannels = GetConfiguration<TargetConfiguration>("target").Users
                .First(x => x.User == message.Author.Id).Servers.First(x => x.Server == guildId).Channels;
            await _restClient.SendMessageAsync(message.ChannelId,
                $"I'm currently listening to the following channels: {string.Join(",", listenedChannels.Select(x => $"<#{x}>"))}");
            return;
        }

        // Outside of anything above, ignore any non-listened channel
        if (!_channelsForUser[message.Author.Id].Contains(message.ChannelId))
            return;

        // Determine if this message should be skipped based on configured skip prefix or current state
        var miscConfig = GetConfiguration<MiscConfiguration>("misc");
        if (rawMessage.StartsWith(miscConfig.Silencer))
            return;
        
        // Check if we have a voice connection for this guild
        if (!VoiceConnections.TryGetValue(guildId, out var voiceClient))
            return;

        // Get the guild from cache
        if (!_discord.Cache.Guilds.TryGetValue(guildId, out var guild))
            return;

        // If the user is not in the vc channel, or not undeafened, ignore them
        if (!guild.VoiceStates.TryGetValue(message.Author.Id, out var userVoiceState) || 
            !userVoiceState.ChannelId.HasValue ||
            userVoiceState.IsSelfDeafened || 
            userVoiceState.IsDeafened)
            return;
        
        // Handle mentioned users
        if (message.MentionedUsers != null)
        {
            foreach (var mention in message.MentionedUsers)
            {
                var member = guild.Users.GetValueOrDefault(mention.Id);
                var displayName = member?.Nickname ?? mention.GlobalName ?? mention.Username;
                rawMessage = rawMessage.Replace($"<@{mention.Id}>", $" at {displayName}");
            }
        }

        // Handle mentioned channels - using regex to find channel mentions
        var channelMentionRegex = ChannelMentionPattern();
        foreach (Match match in channelMentionRegex.Matches(rawMessage))
        {
            if (ulong.TryParse(match.Groups[1].Value, out var channelId))
            {
                if (guild.Channels.TryGetValue(channelId, out var channel))
                {
                    rawMessage = rawMessage.Replace(match.Value, $" at {channel.Name}");
                }
            }
        }

        // Handle mentioned roles
        if (message.MentionedRoleIds != null)
        {
            foreach (var roleId in message.MentionedRoleIds)
            {
                if (guild.Roles.TryGetValue(roleId, out var role))
                {
                    rawMessage = rawMessage.Replace($"<@&{roleId}>", $" at {role.Name}");
                }
            }
        }

        // Strip out emojis
        rawMessage = EmojiPattern().Replace(rawMessage, "");

        // Strip out URLs
        rawMessage = UrlPattern().Replace(rawMessage, "").Trim();

        var openAIConfig = GetConfiguration<OpenAIConfiguration>("openai");

        // Check for attachments to describe
        var imageAttachments = message.Attachments
            .Where(x => x.ContentType != null && x.ContentType.Contains("image"))
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
        var audioResult = await _voiceSynth.SynthesizeTextToSpeechAsync(rawMessage, _voices[message.Author.Id]);
        if (audioResult == null)
            return;

        MessageQueues[guildId].Enqueue(new QueuedMessage(audioResult, guildId, _voiceSynth.AudioFormat));
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
            // Check if we have a voice connection for this guild
            if (VoiceConnections.TryGetValue(guildId, out var voiceClient))
            {
                await SendAudioToVoiceChannel(task.Stream, voiceClient, task.AudioFormat);
            }
            task.Stream.Close();

            // Get rid of the item we were processing afterwards
            MessageQueues[guildId].TryDequeue(out _);
        }
    }

    private static async Task<string> DescribeAttachments(IEnumerable<Attachment> attachments)
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

    private static async ValueTask OnVoiceStateUpdate(VoiceState voiceState)
    {
        if (!_personsOfInterest.Contains(voiceState.UserId) || !_serversOfInterest.Contains(voiceState.GuildId))
            return;

        // Get guild from cache
        if (!_discord.Cache.Guilds.TryGetValue(voiceState.GuildId, out var guild))
            return;

        // Set nickname if an update is needed
        await SetNickname(guild);

        var target = await GetTargetChannel(guild);
        
        if (target is null)
        {
            await DisconnectFromVoiceChannel(voiceState.GuildId);
            return;
        }

        // If we're already in the target channel, don't change anything
        if (VoiceChannelIds.TryGetValue(voiceState.GuildId, out var currentChannelId) && currentChannelId == target.Id)
        {
            return;
        }

        // Connect to the new target channel (this will disconnect from current channel first)
        await ConnectToVoiceChannel(voiceState.GuildId, target.Id);
    }

    private static Task<IGuildChannel?> GetTargetChannel(Guild guild)
    {
        var users = UsersOfInterestForServer(guild.Id).ToHashSet();
        
        // Get voice channel IDs where users of interest are present
        var voiceChannelIds = guild.VoiceStates.Values
            .Where(vs => vs.ChannelId.HasValue && users.Contains(vs.UserId))
            .Select(vs => vs.ChannelId!.Value)
            .Distinct()
            .ToHashSet();
        
        // Get the actual voice channels
        var voiceChannels = guild.Channels.Values
            .Where(x => voiceChannelIds.Contains(x.Id))
            .ToList();
        
        // Score each channel based on users of interest in it
        var channels = voiceChannels
            .Select(channel =>
            {
                // Find users in this voice channel from voice states
                var usersInChannel = guild.VoiceStates.Values
                    .Where(vs => vs.ChannelId == channel.Id && users.Contains(vs.UserId))
                    .ToList();
                
                var score = usersInChannel.Count(vs => !vs.IsSelfDeafened && !vs.IsDeafened);
                
                return new { value = channel, score };
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ToList();

        // No target channels
        if (channels.Count == 0)
        {
            return Task.FromResult<IGuildChannel?>(null);
        }

        // Only take the highest score channel[s]
        var topScore = channels[0].score;
        channels = channels.Where(x => x.score == topScore).ToList();
        
        // If we're already connected to one of the top-scoring channels, prefer staying there
        if (VoiceChannelIds.TryGetValue(guild.Id, out var currentChannelId))
        {
            var currentChannel = channels.FirstOrDefault(c => c.value.Id == currentChannelId);
            if (currentChannel != null)
            {
                return Task.FromResult<IGuildChannel?>(currentChannel.value);
            }
        }

        // Just return the first entry as we have no other tie breaker at the moment
        return Task.FromResult<IGuildChannel?>(channels[0].value);
    }

    private static async ValueTask OnGuildMemberUpdate(GuildUser guildUser)
    {
        if (!_personsOfInterest.Contains(guildUser.Id) || !_serversOfInterest.Contains(guildUser.GuildId))
            return;

        // Get guild from cache
        if (!_discord.Cache.Guilds.TryGetValue(guildUser.GuildId, out var guild))
            return;

        // Set nickname
        await SetNickname(guild);
    }

    private static async Task SetNickname(Guild guild)
    {
        // Get target channel, if any, if none is found don't go further
        var targetChannel = await GetTargetChannel(guild);
        var users = UsersOfInterestForServer(guild.Id).ToHashSet();
        
        string nickname;
        if (targetChannel is null)
        {
            nickname = "Voiceless";
        }
        else
        {
            // Get target users in this channel from voice states
            var targetUsers = guild.VoiceStates.Values
                .Where(vs => vs.ChannelId == targetChannel.Id && 
                            users.Contains(vs.UserId) && 
                            !vs.IsSelfDeafened && 
                            !vs.IsDeafened)
                .ToList();

            nickname = targetUsers.Count switch
            {
                0 => "Voiceless",
                1 => GetNicknameForUser(guild, targetUsers[0].UserId),
                _ => "Multi-User Mic"
            };
        }

        try
        {
            await _restClient.ModifyCurrentGuildUserAsync(guild.Id, x => x.Nickname = nickname);
        }
        catch
        {
            // Ignore errors when setting nickname (e.g., missing permissions)
        }
    }

    private static string GetNicknameForUser(Guild guild, ulong userId)
    {
        var displayName = "Unknown";
        if (guild.Users.TryGetValue(userId, out var member))
        {
            displayName = member.Nickname ?? member.GlobalName ?? member.Username;
        }
        return $"{displayName[..Math.Min(26, displayName.Length)]}'s Mic";
    }

    private static IEnumerable<ulong> UsersOfInterestForServer(ulong server) =>
        GetConfiguration<TargetConfiguration>("target")
            .Users.Where(x => x.Servers.Any(y => y.Server == server)).Select(x => x.User);

    private static async Task ConnectToVoiceChannel(ulong guildId, ulong channelId)
    {
        await VoiceConnectionLock.WaitAsync();
        try
        {
            // Disconnect any existing connection first
            await DisconnectFromVoiceChannelUnsafe(guildId);

            // Join the voice channel
            var voiceClient = await _discord.JoinVoiceChannelAsync(guildId, channelId);

            // Start the voice client
            await voiceClient.StartAsync();

            // Enter speaking state
            await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));

            VoiceConnections[guildId] = voiceClient;
            VoiceChannelIds[guildId] = channelId;
        }
        finally
        {
            VoiceConnectionLock.Release();
        }
    }

    private static async Task DisconnectFromVoiceChannel(ulong guildId)
    {
        await VoiceConnectionLock.WaitAsync();
        try
        {
            await DisconnectFromVoiceChannelUnsafe(guildId);
        }
        finally
        {
            VoiceConnectionLock.Release();
        }
    }

    private static async Task DisconnectFromVoiceChannelUnsafe(ulong guildId)
    {
        VoiceChannelIds.TryRemove(guildId, out _);
        if (VoiceConnections.TryRemove(guildId, out var voiceClient))
        {
            try
            {
                await voiceClient.CloseAsync();
            }
            catch
            {
                // Ignore errors during disconnection
            }
        }
    }

    private static async Task SendAudioToVoiceChannel(Stream inputStream, VoiceClient voiceClient, string audioFormat)
    {
        // Create the output stream for sending to Discord
        var outStream = voiceClient.CreateOutputStream();

        // Create Opus encode stream to convert PCM to Opus
        await using var opusStream = new OpusEncodeStream(outStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio);

        // Start FFmpeg to convert input audio to PCM
        var ffmpeg = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-f {audioFormat} -i pipe:0 -ac 2 -f s16le -ar 48000 pipe:1",
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        });

        if (ffmpeg is null)
            throw new InvalidOperationException("Failed to create FFmpeg instance!");

        // Have to do input and output at the same time otherwise output buffer fills and waits
        var inputTask = Task.Run(async () =>
        {
            inputStream.Seek(0, SeekOrigin.Begin);
            await inputStream.CopyToAsync(ffmpeg.StandardInput.BaseStream);
            ffmpeg.StandardInput.Close();
        });

        var outputTask = Task.Run(async () =>
        {
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(opusStream);
            ffmpeg.StandardOutput.Close();
        });

        await Task.WhenAll(inputTask, outputTask);
        await ffmpeg.WaitForExitAsync();

        // Flush to ensure all data is sent
        await opusStream.FlushAsync();
    }

    private static T GetConfiguration<T>(string section) where T : class, new()
    {
        var toReturn = new T();
        var options = new ConfigureOptions<T>(o => _configuration.GetSection(section).Bind(o));
        options.Configure(toReturn);
        return toReturn;
    }
}