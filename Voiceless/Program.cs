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
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Managers;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels.ResponseModels;
using Voiceless.Configuration;
using DiscordConfiguration = Voiceless.Configuration.DiscordConfiguration;

namespace Voiceless;

public static partial class Program
{
    [GeneratedRegex(@"\b(?:https?://)?(?:www\.)?[a-zA-Z0-9-]+(?:\.[a-zA-Z]{2,})+(?:/[^\s]*)?\b", RegexOptions.Multiline,
        "en-US")]
    private static partial Regex UrlPattern();

    [GeneratedRegex("<a?:(?<text>.+):[0-9]+>", RegexOptions.Multiline, "en-US")]
    private static partial Regex EmojiPattern();

    private static OpenAIService _openAi = null!;
    private static IConfiguration _configuration = null!;
    private static readonly HashSet<string> AllowedModels = [];
    private static readonly HashSet<string> AllowedVoices = [];
    private static readonly HashSet<string> ImageDetailLevels = [];

    private static readonly ConcurrentQueue<(Stream stream, VoiceNextConnection voiceChannel)> MessageQueue = new();

    public static async Task Main()
    {
        // Setup allowed models and voices
        foreach (var property in typeof(Models).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            var propertyValue = (string?)property.GetValue(null);
            if (property.PropertyType == typeof(string) && !string.IsNullOrWhiteSpace(propertyValue) &&
                propertyValue.StartsWith("tts", StringComparison.InvariantCultureIgnoreCase))
                AllowedModels.Add(propertyValue);
        }

        foreach (var property in typeof(StaticValues.AudioStatics.Voice).GetProperties(BindingFlags.Public |
                     BindingFlags.Static))
        {
            var propertyValue = (string?)property.GetValue(null);
            if (property.PropertyType == typeof(string) && !string.IsNullOrWhiteSpace(propertyValue))
                AllowedVoices.Add(propertyValue);
        }

        foreach (var property in typeof(StaticValues.ImageStatics.ImageDetailTypes).GetProperties(BindingFlags.Public |
                     BindingFlags.Static))
        {
            var propertyValue = (string?)property.GetValue(null);
            if (property.PropertyType == typeof(string) && !string.IsNullOrWhiteSpace(propertyValue))
                ImageDetailLevels.Add(propertyValue);
        }

        // Get config
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true, reloadOnChange: true);
        _configuration = builder.Build();

        // Grab configuration objects
        var discordConfig = GetConfiguration<DiscordConfiguration>("discord");
        var openAIConfig = GetConfiguration<OpenAIConfiguration>("openai");

        var miscConfig = GetConfiguration<MiscConfiguration>("misc");

        _openAi = new OpenAIService(new OpenAiOptions
        {
            ApiKey = openAIConfig.Token ??
                     throw new InvalidOperationException("Invalid configuration, missing OpenAI API token")
        });

        var discord = new DiscordClient(new DSharpPlus.DiscordConfiguration
        {
            Token = discordConfig.Token ??
                    throw new InvalidOperationException("Invalid configuration, missing Discord bot token"),
            TokenType = TokenType.Bot,
            Intents = DiscordIntents.AllUnprivileged | DiscordIntents.MessageContents |
                      DiscordIntents.GuildVoiceStates | DiscordIntents.GuildMembers
        });
        discord.UseVoiceNext();

        discord.VoiceStateUpdated += VoiceStateUpdated;
        discord.MessageCreated += MessageCreated;
        discord.SessionCreated += SessionCreated;

        await discord.ConnectAsync();
        await Task.Delay(Timeout.Infinite);
    }

    private static async Task SessionCreated(DiscordClient sender, SessionReadyEventArgs args)
    {
        var targetConfig = GetConfiguration<TargetConfiguration>("target");

        // Update nickname in target servers, note if active server exists
        DiscordChannel? targetChannel = null;
        foreach (var server in targetConfig.Servers)
        {
            DiscordGuild foundServer;
            try
            {
                foundServer = await sender.GetGuildAsync(server.Server);
            }
            catch (NotFoundException)
            {
                continue;
            }

            // Get target user in this guild
            DiscordMember targetUser;
            try
            {
                targetUser = await foundServer.GetMemberAsync(targetConfig.User);
            }
            catch (ServerErrorException)
            {
                continue;
            }

            await (await foundServer.GetMemberAsync(sender.CurrentUser.Id)).ModifyAsync(x =>
                x.Nickname = $"{targetUser.Nickname}'s Microphone");

            // Check if there's an active vc with them in it
            if (targetChannel is not null)
                continue;
            var channels = await foundServer.GetChannelsAsync();
            targetChannel = channels.FirstOrDefault(x =>
                x.Type == DiscordChannelType.Voice && x.Users.Any(y => y.Id == targetConfig.User && !y.IsDeafened));
        }

        if (targetChannel is not null)
            await targetChannel.ConnectAsync();
    }

    private static async Task MessageCreated(DiscordClient sender, MessageCreateEventArgs args)
    {
        var targetConfig = GetConfiguration<TargetConfiguration>("target");

        if (!targetConfig.Servers.Any(x => x.Channels.Contains(args.Channel.Id)) || args.Author.Id != targetConfig.User)
            return;
        var vcChannel = sender.GetVoiceNext().GetConnection(args.Guild);
        if (vcChannel == null)
            return;

        var rawMessage = args.Message.Content;

        // Determine if this message should be skipped based on configured skip prefix or current state
        var miscConfig = GetConfiguration<MiscConfiguration>("misc");
        if (rawMessage.StartsWith(miscConfig.Silencer))
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
            rawMessage = rawMessage.Replace($"<#{mention.Id}>", $" at {(await args.Guild.GetChannelAsync(mention.Id)).Name}");
        }
        
        // Handle mentioned roles
        rawMessage = args.Message.MentionedRoles.Aggregate(rawMessage,
            (current, mention) => current.Replace($"<@&{mention.Id}>", $" at {args.Guild.GetRole(mention.Id).Name}"));

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
        var audioResult = await RunTTS(rawMessage);
        if (audioResult is not { Successful: true, Data: not null })
            return;

        MessageQueue.Enqueue((audioResult.Data, vcChannel));
        if (MessageQueue.Count == 1)
            await ProcessMessageQueue();
    }

    private static async Task<string> ApplyFlavorPrompt(string rawMessage)
    {
        var openAIConfig = GetConfiguration<OpenAIConfiguration>("openai");

        var completionResult = await _openAi.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromSystem(openAIConfig.FlavorPrompt),
                ChatMessage.FromUser(rawMessage)
            },
            Model = Models.Gpt_4o
        });

        return completionResult.Successful
            ? completionResult.Choices.First().Message.Content ?? "Flavor prompt application returned a null response"
            : "Failed to apply flavor prompt.";
    }

    private static async Task ProcessMessageQueue()
    {
        while (MessageQueue.TryPeek(out var task))
        {
            // Send it out!
            var transmit = task.voiceChannel.GetTransmitSink();
            await ConvertAudioToPcm(task.stream, transmit);
            task.stream.Close();

            // Get rid of the item we were processing afterwards
            MessageQueue.TryDequeue(out _);
        }
    }

    private static async Task<string> DescribeAttachments(IEnumerable<DiscordAttachment> attachments)
    {
        var openAIConfig = GetConfiguration<OpenAIConfiguration>("openai");

        var completionResult = await _openAi.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromSystem(openAIConfig.AttachmentPrompt),
                ChatMessage.FromUser(attachments.Select(x =>
                        MessageContent.ImageUrlContent(x.Url!, openAIConfig.AttachmentDetail))
                    .ToList())
            },
            MaxTokens = openAIConfig.MaxAttachmentTokens,
            Model = Models.Gpt_4o,
            N = 1
        });

        return completionResult.Successful
            ? completionResult.Choices.First().Message.Content ?? "Attachment analysis API returned a null response"
            : "Failed to describe attached images.";
    }

    // ReSharper disable once InconsistentNaming
    private static async Task<AudioCreateSpeechResponse<Stream>> RunTTS(string text)
    {
        var openAIConfig = GetConfiguration<OpenAIConfiguration>("openai");

        return await _openAi.Audio.CreateSpeech<Stream>(new AudioCreateSpeechRequest
        {
            Model = openAIConfig.Model,
            Input = text,
            Voice = openAIConfig.Voice,
            ResponseFormat = StaticValues.AudioStatics.CreateSpeechResponseFormat.Opus,
            Speed = 1f
        });
    }


    private static async Task VoiceStateUpdated(DiscordClient sender, VoiceStateUpdateEventArgs args)
    {
        var targetConfig = GetConfiguration<TargetConfiguration>("target");
        if (args.User.Id != targetConfig.User)
            return;

        // Gotta get in there....
        if (args.After is { Channel: not null, IsSelfDeafened: false, IsServerDeafened: false })
        {
            var existingConnection = sender.GetVoiceNext().GetConnection(args.Guild);
            if (existingConnection != null)
            {
                // Don't do anything if we're already in the channel
                if (existingConnection.TargetChannel.Id == args.After.Channel.Id)
                    return;

                // Otherwise disconnect from this old one
                existingConnection.Disconnect();
            }

            // Connect to the new channel
            await args.Channel.ConnectAsync();
        }
        else
        {
            // Exit out of the channel if the target is now muted
            sender.GetVoiceNext().GetConnection(args.Guild)?.Disconnect();
        }
    }

    private static async Task ConvertAudioToPcm(Stream inputStream, VoiceTransmitSink outputStream)
    {
        var ffmpeg = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = "-f ogg -i pipe:0 -ac 2 -f s16le -ar 48000 pipe:1",
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