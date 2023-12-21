using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.VoiceNext;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Managers;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels.ResponseModels;

namespace Voiceless;

public class Program
{
    private static Regex _urlPattern =
        new Regex(@"\b(?:https?://)?(?:www\.)?[a-zA-Z0-9-]+(?:\.[a-zA-Z]{2,})+(?:/[^\s]*)?\b",
            RegexOptions.Compiled | RegexOptions.Multiline);

    private static Regex _emojiPattern =
        new Regex(@"<:(?<text>.+):[0-9]+>", RegexOptions.Compiled | RegexOptions.Multiline);

    private static OpenAIService _openAi = null!;
    private static IConfiguration _configuration = null!;
    private static readonly HashSet<string> AllowedModels = [];
    private static readonly HashSet<string> AllowedVoices = [];
    private static readonly HashSet<string> ImageDetailLevels = [];

    private static readonly ConcurrentQueue<(Stream stream, VoiceNextConnection voiceChannel)> _messageQueue = new();

    public static async Task Main(string[] args)
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

        _openAi = new OpenAIService(new OpenAiOptions()
        {
            ApiKey = _configuration["openai:token"] ??
                     throw new InvalidOperationException("Invalid configuration, missing OpenAI API token")
        });

        var discord = new DiscordClient(new DiscordConfiguration()
        {
            Token = _configuration["discord:token"] ??
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
        // Check if target user is in vc already
        var server = await sender.GetGuildAsync(GetServerId());

        // Set nickname
        var targetUser = await server.GetMemberAsync(GetUserId());
        await (await server.GetMemberAsync(sender.CurrentUser.Id)).ModifyAsync(x =>
            x.Nickname = $"{targetUser.Nickname ?? targetUser.DisplayName ?? "Someone"}'s Microphone");

        var channels = await server.GetChannelsAsync();
        var targetChannel = channels.FirstOrDefault(x =>
            x.Type == ChannelType.Voice && x.Users.Any(y => y.Id == GetUserId() && !y.IsDeafened));
        if (targetChannel is not null)
            await targetChannel.ConnectAsync();
    }

    private static async Task MessageCreated(DiscordClient sender, MessageCreateEventArgs args)
    {
        if (args.Channel.Id != GetTextChannelId() || args.Author.Id != GetUserId())
            return;
        var vcChannel = (VoiceNextConnection?)sender.GetVoiceNext().GetConnection(args.Guild);
        if (vcChannel == null)
            return;

        var rawMessage = args.Message.Content;
        foreach (var mention in args.Message.MentionedUsers)
        {
            rawMessage = rawMessage.Replace($"<@{mention.Id}>",
                $" at {(await args.Guild.GetMemberAsync(mention.Id)).DisplayName}");
        }

        foreach (var mention in args.Message.MentionedChannels)
        {
            rawMessage = rawMessage.Replace($"<#{mention.Id}>",
                $" at {args.Guild.GetChannel(mention.Id).Name}");
        }

        foreach (var mention in args.Message.MentionedRoles)
        {
            rawMessage = rawMessage.Replace($"<@&{mention.Id}>",
                $" at {args.Guild.GetRole(mention.Id).Name}");
        }

        // Sort out emojis
        _emojiPattern.Replace(rawMessage, x => x.Groups["text"].Value);

        // Strip out URLs
        rawMessage = _urlPattern.Replace(rawMessage, "").Trim();

        // Check for attachments to describe
        var imageAttachments = args.Message.Attachments.Where(x => x.MediaType.Contains("image")).ToList();
        if (imageAttachments.Count != 0)
            rawMessage += (string.IsNullOrWhiteSpace(rawMessage) ? string.Empty : ". ") +
                          await DescribeAttachments(imageAttachments);

        // Abort here if it's a basically blank message
        if (string.IsNullOrWhiteSpace(rawMessage))
            return;

        // Perform TTS conversion
        var audioResult = await RunTTS(rawMessage);
        if (audioResult is not { Successful: true, Data: not null })
            return;

        _messageQueue.Enqueue((audioResult.Data, vcChannel));
        if (_messageQueue.Count == 1)
            await ProcessMessageQueue();
    }

    private static async Task ProcessMessageQueue()
    {
        while (_messageQueue.TryPeek(out var task))
        {
            // Send it out!
            var transmit = task.voiceChannel.GetTransmitSink();
            await ConvertAudioToPcm(task.stream, transmit);
            task.stream.Close();
            
            // Get rid of the item we were processing afterwards
            _messageQueue.TryDequeue(out _);
        }
    }

    private static async Task<string> DescribeAttachments(IEnumerable<DiscordAttachment> attachments)
    {
        var completionResult = await _openAi.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest()
        {
            Messages = new List<ChatMessage>()
            {
                ChatMessage.FromSystem(
                    "Provide a brief, clear description of each image, focusing on the main subjects and their context. Describe only the most prominent and relevant elements, avoiding detailed or complex descriptions. Summarize each scene in one or two sentences, emphasizing what is essential for understanding the image's primary content and context. Include the total count of the images at the start."),
                ChatMessage.FromUser(attachments.Select(x =>
                        MessageContent.ImageUrlContent(x.Url, GetAttachmentDetail()))
                    .ToList())
            },
            MaxTokens = GetMaxAttachmentTokens(),
            Model = Models.Gpt_4_vision_preview,
            N = 1
        });

        return completionResult.Successful
            ? (completionResult.Choices.First().Message.Content ?? "Attachment analysis API returned a null response")
            : "Failed to describe attached images.";
    }

    private static async Task<AudioCreateSpeechResponse<Stream>> RunTTS(string text) =>
        await _openAi.Audio.CreateSpeech<Stream>(new AudioCreateSpeechRequest()
        {
            Model = GetVoiceModel(),
            Input = text,
            Voice = GetVoice(),
            ResponseFormat = StaticValues.AudioStatics.CreateSpeechResponseFormat.Opus,
            Speed = 1f
        });

    private static async Task VoiceStateUpdated(DiscordClient sender, VoiceStateUpdateEventArgs args)
    {
        if (args.User.Id != GetUserId())
            return;

        // Gotta get in there....
        if (args.After is { Channel: not null, IsSelfDeafened: false, IsServerDeafened: false })
        {
            var existingConnection = sender.GetVoiceNext().GetConnection(args.Guild);
            if (existingConnection != null)
            {
                // Dont do anything if we're already in the channel
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

    private static ulong GetUserId()
        => ulong.TryParse(_configuration["target:user"], out var userId)
            ? userId
            : throw new InvalidOperationException("Target user ID is missing or invalid");

    private static ulong GetServerId()
        => ulong.TryParse(_configuration["target:server"], out var userId)
            ? userId
            : throw new InvalidOperationException("Target server ID is missing or invalid");

    private static ulong GetTextChannelId()
        => ulong.TryParse(_configuration["target:text_channel"], out var userId)
            ? userId
            : throw new InvalidOperationException("Target text channel ID is missing or invalid");

    private static string GetVoiceModel()
    {
        var configured = _configuration["openai:model"];
        if (configured == null || !AllowedModels.Contains(configured))
            throw new InvalidOperationException(
                $"Configured model is invalid or missing. Valid models: {string.Join(", ", AllowedModels)}");
        return configured;
    }

    private static string GetVoice()
    {
        var configured = _configuration["openai:voice"];
        if (configured == null || !AllowedVoices.Contains(configured))
            throw new InvalidOperationException(
                $"Configured voice is invalid or missing. Valid voices: {string.Join(", ", AllowedVoices)}");
        return configured;
    }

    private static string GetAttachmentDetail()
    {
        var configured = _configuration["openai:attachment_detail"];
        if (configured == null || !ImageDetailLevels.Contains(configured))
            throw new InvalidOperationException(
                $"Attachment detail level is invalid or missing. Valid levels: {string.Join(", ", ImageDetailLevels)}");
        return configured;
    }

    private static int GetMaxAttachmentTokens() =>
        int.TryParse(_configuration["openai:max_attachment_tokens"], out var attachmentTokens)
            ? attachmentTokens
            : throw new InvalidOperationException("Max attachment tokens is missing or invalid");
}