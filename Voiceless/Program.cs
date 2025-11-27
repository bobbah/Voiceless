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
using Serilog;
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
    
    // Track voice states of target users ourselves instead of relying on gateway cache
    // Key: (guildId, userId), Value: voice state info
    private static readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), TrackedVoiceState> TrackedVoiceStates = new();
    
    // Simple record to track the voice state info we care about
    private record TrackedVoiceState(ulong? ChannelId, bool IsSelfDeafened, bool IsDeafened);

    public static async Task Main()
    {
        // Configure Serilog - use Information level by default for reasonable output
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            var version = GetVersionInfo();
            Log.Information("Voiceless {Version} starting up...", version);

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

            Log.Information("Configuration loaded. Monitoring {UserCount} users across {ServerCount} servers", 
                _personsOfInterest.Count, _serversOfInterest.Count);

            // Use ElevenLabs when available
            var elevenLabsConfig = GetConfiguration<ElevenLabsConfiguration>("elevenlabs");
            if (!string.IsNullOrEmpty(elevenLabsConfig?.Token))
            {
                Log.Information("Using ElevenLabs for TTS synthesis");
                var synth = new ElevenLabsVoiceSynthesizer(elevenLabsConfig);
                await synth.ConfigureClient();
                _voiceSynth = synth;
            }
            else
            {
                Log.Information("Using OpenAI for TTS synthesis");
                _voiceSynth = new OpenAIVoiceSynthesizer(_openAi, openAIConfig);
            }

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

            Log.Information("Connecting to Discord...");

            // Connect and run
            await _discord.StartAsync();
            
            Log.Information("Connected to Discord Gateway. Bot is now running.");
            
            await Task.Delay(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async ValueTask OnReady(ReadyEventArgs args)
    {
        Log.Information("Discord Ready event received. Bot user: {Username}#{Discriminator}", 
            args.User.Username, args.User.Discriminator);
        
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

        Log.Debug("Processing {ServerCount} configured servers", servers.Count);

        foreach (var server in servers)
        {
            var restGuild = await _discord.Rest.GetGuildAsync(server.Key);
            if (!_discord.Cache.Guilds.TryGetValue(server.Key, out var foundServer))
            {
                Log.Warning("Server {ServerId} not found in cache", server.Key);
                continue;
            }
            
            Log.Debug("Processing server: {ServerName} ({ServerId})", foundServer.Name, foundServer.Id);
            Log.Debug("Voice states in server: {VoiceStateCount}", foundServer.VoiceStates.Count);
            
            // Initialize our internal voice state tracking from the initial cache
            // This is only done once at startup; subsequent updates come from VoiceStateUpdate events
            foreach (var vs in foundServer.VoiceStates)
            {
                Log.Debug("  Voice state - User: {UserId}, Channel: {ChannelId}", vs.Key, vs.Value.ChannelId);
                
                // Only track target users
                if (_personsOfInterest.Contains(vs.Key) && vs.Value.ChannelId.HasValue)
                {
                    var key = (foundServer.Id, vs.Key);
                    TrackedVoiceStates[key] = new TrackedVoiceState(
                        vs.Value.ChannelId.Value,
                        vs.Value.IsSelfDeafened,
                        vs.Value.IsDeafened);
                    Log.Debug("  Initialized tracking for user {UserId} in channel {ChannelId}", vs.Key, vs.Value.ChannelId.Value);
                }
            }

            // Get target channel, if any, if none is found don't go further
            await SetNickname(foundServer);
            var targetChannel = await GetTargetChannel(foundServer);
            if (targetChannel is null)
            {
                Log.Debug("No target channel found for server {ServerName}", foundServer.Name);
                continue;
            }

            Log.Information("Connecting to voice channel {ChannelName} ({ChannelId}) in {ServerName}", 
                targetChannel.Name, targetChannel.Id, foundServer.Name);
            
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
        
        Log.Debug("OnMessageCreate: Message from user {UserId} in guild {GuildId}: {Content}", 
            message.Author.Id, guildId, message.Content.Length > 50 ? message.Content[..50] + "..." : message.Content);
        
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
        {
            Log.Debug("OnMessageCreate: Channel {ChannelId} not in listened channels for user", message.ChannelId);
            return;
        }

        // Determine if this message should be skipped based on configured skip prefix or current state
        var miscConfig = GetConfiguration<MiscConfiguration>("misc");
        if (rawMessage.StartsWith(miscConfig.Silencer))
        {
            Log.Debug("OnMessageCreate: Message starts with silencer prefix, skipping");
            return;
        }
        
        // Check if we have a voice connection for this guild
        if (!VoiceConnections.TryGetValue(guildId, out var voiceClient))
        {
            Log.Debug("OnMessageCreate: No voice connection for guild {GuildId}", guildId);
            return;
        }

        // Get the guild from cache
        if (!_discord.Cache.Guilds.TryGetValue(guildId, out var guild))
        {
            Log.Warning("OnMessageCreate: Guild {GuildId} not found in cache", guildId);
            return;
        }

        // If the user is not in the vc channel, or not undeafened, ignore them
        // Use our internal tracked voice states instead of the gateway cache
        var userVoiceStateKey = (guildId, message.Author.Id);
        if (!TrackedVoiceStates.TryGetValue(userVoiceStateKey, out var trackedUserState) ||
            !trackedUserState.ChannelId.HasValue ||
            trackedUserState.IsSelfDeafened || 
            trackedUserState.IsDeafened)
        {
            Log.Debug("OnMessageCreate: User {UserId} not in voice or is deafened", message.Author.Id);
            return;
        }
        
        Log.Information("OnMessageCreate: Processing TTS for message from {UserId}", message.Author.Id);
        
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

        // Extract voice instructions from the message if present (e.g., %sensually%)
        var (cleanedMessage, instructions) = InstructionParser.ExtractInstructions(rawMessage);
        rawMessage = cleanedMessage;
        if (instructions != null)
        {
            Log.Debug("OnMessageCreate: Extracted instructions '{Instructions}' from message", instructions);
        }

        // Abort here if the message is now empty after extracting instructions
        if (string.IsNullOrWhiteSpace(rawMessage))
            return;

        // Perform TTS conversion
        Log.Debug("OnMessageCreate: Synthesizing TTS for message");
        var audioResult = await _voiceSynth.SynthesizeTextToSpeechAsync(rawMessage, _voices[message.Author.Id], instructions);
        if (audioResult == null)
        {
            Log.Warning("OnMessageCreate: TTS synthesis returned null");
            return;
        }

        Log.Debug("OnMessageCreate: Enqueueing audio for playback");
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
        Log.Debug("ProcessMessageQueue: Starting to process queue for guild {GuildId}", guildId);
        
        while (MessageQueues[guildId].TryPeek(out var task))
        {
            // Check if we have a voice connection for this guild
            if (VoiceConnections.TryGetValue(guildId, out var voiceClient))
            {
                Log.Debug("ProcessMessageQueue: Sending audio to voice channel");
                await SendAudioToVoiceChannel(task.Stream, voiceClient, task.AudioFormat);
                Log.Debug("ProcessMessageQueue: Audio sent successfully");
            }
            else
            {
                Log.Warning("ProcessMessageQueue: No voice connection available for guild {GuildId}", guildId);
            }
            task.Stream.Close();

            // Get rid of the item we were processing afterwards
            MessageQueues[guildId].TryDequeue(out _);
        }
        
        Log.Debug("ProcessMessageQueue: Queue processing complete for guild {GuildId}", guildId);
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
        Log.Debug("VoiceStateUpdate: User {UserId} in Guild {GuildId}, Channel: {ChannelId}", 
            voiceState.UserId, voiceState.GuildId, voiceState.ChannelId);
        
        // Only track target users in monitored servers
        if (!_personsOfInterest.Contains(voiceState.UserId) || !_serversOfInterest.Contains(voiceState.GuildId))
        {
            Log.Debug("VoiceStateUpdate: User {UserId} not a person of interest or server not monitored", voiceState.UserId);
            return;
        }
        
        // Update our internal voice state tracking immediately from the event
        UpdateTrackedVoiceState(voiceState);

        // Get guild from cache (only needed for channel names and nickname setting, not voice state)
        if (!_discord.Cache.Guilds.TryGetValue(voiceState.GuildId, out var guild))
        {
            Log.Warning("VoiceStateUpdate: Guild {GuildId} not found in cache", voiceState.GuildId);
            return;
        }

        Log.Debug("VoiceStateUpdate: Processing for guild {GuildName}", guild.Name);
        
        // Set nickname if an update is needed
        await SetNickname(guild);

        // Get target channel using our internal tracking (no longer passing override since we track it ourselves)
        var target = await GetTargetChannel(guild);
        
        if (target is null)
        {
            Log.Debug("VoiceStateUpdate: No target channel found, disconnecting from guild {GuildName}", guild.Name);
            await DisconnectFromVoiceChannel(voiceState.GuildId);
            return;
        }

        Log.Debug("VoiceStateUpdate: Target channel is {ChannelName} ({ChannelId})", target.Name, target.Id);

        // If we're already in the target channel, don't change anything
        if (VoiceChannelIds.TryGetValue(voiceState.GuildId, out var currentChannelId) && currentChannelId == target.Id)
        {
            Log.Debug("VoiceStateUpdate: Already in target channel {ChannelId}", currentChannelId);
            return;
        }

        Log.Information("VoiceStateUpdate: Connecting to voice channel {ChannelName} in {GuildName}", 
            target.Name, guild.Name);
        
        // Connect to the new target channel (this will disconnect from current channel first)
        await ConnectToVoiceChannel(voiceState.GuildId, target.Id);
    }
    
    /// <summary>
    /// Updates our internal tracking of voice states from incoming VoiceStateUpdate events.
    /// This allows us to avoid relying on the gateway cache which may be stale.
    /// </summary>
    private static void UpdateTrackedVoiceState(VoiceState voiceState)
    {
        var key = (voiceState.GuildId, voiceState.UserId);
        
        if (voiceState.ChannelId.HasValue)
        {
            // User is in a voice channel - update or add their state
            TrackedVoiceStates[key] = new TrackedVoiceState(
                voiceState.ChannelId.Value,
                voiceState.IsSelfDeafened,
                voiceState.IsDeafened);
            Log.Debug("UpdateTrackedVoiceState: Updated state for user {UserId} in guild {GuildId}: Channel={ChannelId}, SelfDeaf={SelfDeaf}, Deaf={Deaf}",
                voiceState.UserId, voiceState.GuildId, voiceState.ChannelId.Value, voiceState.IsSelfDeafened, voiceState.IsDeafened);
        }
        else
        {
            // User disconnected from voice - remove their state
            TrackedVoiceStates.TryRemove(key, out _);
            Log.Debug("UpdateTrackedVoiceState: Removed state for user {UserId} in guild {GuildId} (disconnected)",
                voiceState.UserId, voiceState.GuildId);
        }
    }

    private static Task<IGuildChannel?> GetTargetChannel(Guild guild)
    {
        var users = UsersOfInterestForServer(guild.Id).ToHashSet();
        
        // Use our internal tracked voice states instead of the gateway cache
        var trackedStates = TrackedVoiceStates
            .Where(kvp => kvp.Key.GuildId == guild.Id && users.Contains(kvp.Key.UserId))
            .Select(kvp => new { UserId = kvp.Key.UserId, State = kvp.Value })
            .ToList();
        
        // Get voice channel IDs where users of interest are present
        var voiceChannelIds = trackedStates
            .Where(ts => ts.State.ChannelId.HasValue)
            .Select(ts => ts.State.ChannelId!.Value)
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
                // Find users in this voice channel from our tracked states
                var usersInChannel = trackedStates
                    .Where(ts => ts.State.ChannelId == channel.Id)
                    .ToList();
                
                var score = usersInChannel.Count(ts => !ts.State.IsSelfDeafened && !ts.State.IsDeafened);
                
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
        
        // Use our internal tracked voice states instead of the gateway cache
        var trackedStates = TrackedVoiceStates
            .Where(kvp => kvp.Key.GuildId == guild.Id && users.Contains(kvp.Key.UserId))
            .Select(kvp => new { UserId = kvp.Key.UserId, State = kvp.Value })
            .ToList();
        
        string nickname;
        if (targetChannel is null)
        {
            nickname = "Voiceless";
            Log.Debug("SetNickname: No target channel, setting nickname to 'Voiceless' in {GuildName}", guild.Name);
        }
        else
        {
            // Get target users in this channel from our tracked voice states
            var targetUsers = trackedStates
                .Where(ts => ts.State.ChannelId == targetChannel.Id && 
                            !ts.State.IsSelfDeafened && 
                            !ts.State.IsDeafened)
                .ToList();

            Log.Debug("SetNickname: Found {UserCount} undeafened target users in channel {ChannelName}", 
                targetUsers.Count, targetChannel.Name);

            nickname = targetUsers.Count switch
            {
                0 => "Voiceless",
                1 => GetNicknameForUser(guild, targetUsers[0].UserId),
                _ => "Multi-User Mic"
            };
            
            Log.Debug("SetNickname: Setting nickname to '{Nickname}' in {GuildName}", nickname, guild.Name);
        }

        try
        {
            await _restClient.ModifyCurrentGuildUserAsync(guild.Id, x => x.Nickname = nickname);
            Log.Information("Set nickname to '{Nickname}' in {GuildName}", nickname, guild.Name);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set nickname in {GuildName}", guild.Name);
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
        Log.Debug("ConnectToVoiceChannel: Attempting to connect to channel {ChannelId} in guild {GuildId}", 
            channelId, guildId);
        
        await VoiceConnectionLock.WaitAsync();
        try
        {
            // Disconnect any existing connection first
            await DisconnectFromVoiceChannelUnsafe(guildId);

            Log.Debug("ConnectToVoiceChannel: Joining voice channel...");
            
            // Join the voice channel
            var voiceClient = await _discord.JoinVoiceChannelAsync(guildId, channelId);

            Log.Debug("ConnectToVoiceChannel: Starting voice client...");
            
            // Start the voice client
            await voiceClient.StartAsync();

            Log.Debug("ConnectToVoiceChannel: Entering speaking state...");
            
            // Enter speaking state
            await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));

            VoiceConnections[guildId] = voiceClient;
            VoiceChannelIds[guildId] = channelId;
            
            Log.Information("ConnectToVoiceChannel: Successfully connected to channel {ChannelId} in guild {GuildId}", 
                channelId, guildId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ConnectToVoiceChannel: Failed to connect to channel {ChannelId} in guild {GuildId}", 
                channelId, guildId);
            throw;
        }
        finally
        {
            VoiceConnectionLock.Release();
        }
    }

    private static async Task DisconnectFromVoiceChannel(ulong guildId)
    {
        Log.Debug("DisconnectFromVoiceChannel: Disconnecting from guild {GuildId}", guildId);
        
        await VoiceConnectionLock.WaitAsync();
        try
        {
            await DisconnectFromVoiceChannelUnsafe(guildId);
            Log.Information("DisconnectFromVoiceChannel: Disconnected from guild {GuildId}", guildId);
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
                Log.Debug("DisconnectFromVoiceChannelUnsafe: Closed voice client for guild {GuildId}", guildId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DisconnectFromVoiceChannelUnsafe: Error closing voice client for guild {GuildId}", guildId);
            }
        }
        
        // Send a voice state update to Discord to properly leave the voice channel
        // Passing null for the channel ID signals that we want to disconnect
        try
        {
            await _discord.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null));
            Log.Debug("DisconnectFromVoiceChannelUnsafe: Sent voice state update to leave channel in guild {GuildId}", guildId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DisconnectFromVoiceChannelUnsafe: Error sending voice state update for guild {GuildId}", guildId);
        }
    }

    private static async Task SendAudioToVoiceChannel(Stream inputStream, VoiceClient voiceClient, string audioFormat)
    {
        var streamLength = inputStream.CanSeek ? inputStream.Length : -1;
        Log.Debug("SendAudioToVoiceChannel: Starting audio transmission, format: {Format}, input stream length: {Length}", 
            audioFormat, streamLength);
        
        // Start FFmpeg to convert input audio to PCM - do this FIRST before creating streams
        Log.Debug("SendAudioToVoiceChannel: Creating FFmpeg process start info...");
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        // Build arguments
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(audioFormat);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("pipe:0");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("s16le");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("48000");
        startInfo.ArgumentList.Add("pipe:1");

        Log.Debug("SendAudioToVoiceChannel: Starting FFmpeg process...");
        var ffmpeg = Process.Start(startInfo);

        if (ffmpeg is null)
        {
            Log.Error("SendAudioToVoiceChannel: Failed to create FFmpeg instance!");
            throw new InvalidOperationException("Failed to create FFmpeg instance!");
        }

        Log.Debug("SendAudioToVoiceChannel: FFmpeg process started (PID: {Pid})", ffmpeg.Id);
        
        // Create the output stream for sending to Discord
        Log.Debug("SendAudioToVoiceChannel: Creating Discord output stream...");
        var outStream = voiceClient.CreateOutputStream();
        Log.Debug("SendAudioToVoiceChannel: Discord output stream created, type: {Type}", outStream.GetType().Name);

        // Create Opus encode stream to convert PCM to Opus
        // Run this on a background thread with a timeout in case it blocks
        Log.Debug("SendAudioToVoiceChannel: Creating Opus encode stream (with timeout protection)...");
        OpusEncodeStream? opusStream = null;
        try
        {
            var createOpusTask = Task.Run(() =>
            {
                Log.Debug("SendAudioToVoiceChannel: Inside Task.Run creating OpusEncodeStream...");
                var stream = new OpusEncodeStream(outStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio);
                Log.Debug("SendAudioToVoiceChannel: OpusEncodeStream constructor completed");
                return stream;
            });
            
            // Wait with a timeout
            if (await Task.WhenAny(createOpusTask, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) == createOpusTask)
            {
                opusStream = await createOpusTask.ConfigureAwait(false);
                Log.Debug("SendAudioToVoiceChannel: Opus encode stream created successfully");
            }
            else
            {
                Log.Error("SendAudioToVoiceChannel: OpusEncodeStream creation timed out after 10 seconds!");
                ffmpeg.Kill();
                throw new TimeoutException("OpusEncodeStream creation timed out");
            }
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            Log.Error(ex, "SendAudioToVoiceChannel: Error creating OpusEncodeStream");
            ffmpeg.Kill();
            throw;
        }

        try
        {
            // Read stderr in background to prevent buffer blocking
            var stderrTask = Task.Run(async () =>
            {
                var stderr = await ffmpeg.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Log.Warning("SendAudioToVoiceChannel: FFmpeg stderr: {Stderr}", stderr);
                }
                else
                {
                    Log.Debug("SendAudioToVoiceChannel: FFmpeg stderr was empty");
                }
            });

            // Have to do input and output at the same time otherwise output buffer fills and waits
            var inputTask = Task.Run(async () =>
            {
                try
                {
                    Log.Debug("SendAudioToVoiceChannel: Starting to write input to FFmpeg, stream can seek: {CanSeek}", inputStream.CanSeek);
                    if (inputStream.CanSeek)
                    {
                        inputStream.Seek(0, SeekOrigin.Begin);
                        Log.Debug("SendAudioToVoiceChannel: Stream reset to beginning, copying {Length} bytes to FFmpeg stdin", inputStream.Length);
                    }
                    else
                    {
                        Log.Debug("SendAudioToVoiceChannel: Stream does not support seeking, copying to FFmpeg stdin");
                    }
                    await inputStream.CopyToAsync(ffmpeg.StandardInput.BaseStream).ConfigureAwait(false);
                    Log.Debug("SendAudioToVoiceChannel: Copied data to FFmpeg stdin, flushing...");
                    await ffmpeg.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
                    Log.Debug("SendAudioToVoiceChannel: Flushed FFmpeg stdin, closing...");
                    ffmpeg.StandardInput.Close();
                    Log.Debug("SendAudioToVoiceChannel: Finished writing input to FFmpeg");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "SendAudioToVoiceChannel: Error writing to FFmpeg stdin");
                    throw;
                }
            });

            var outputTask = Task.Run(async () =>
            {
                try
                {
                    Log.Debug("SendAudioToVoiceChannel: Starting to read output from FFmpeg and copy to opus stream");
                    await ffmpeg.StandardOutput.BaseStream.CopyToAsync(opusStream).ConfigureAwait(false);
                    Log.Debug("SendAudioToVoiceChannel: Finished reading output from FFmpeg");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "SendAudioToVoiceChannel: Error reading from FFmpeg stdout");
                    throw;
                }
            });

            Log.Debug("SendAudioToVoiceChannel: Waiting for input and output tasks to complete...");
            // Wait for both input and output to complete
            await Task.WhenAll(inputTask, outputTask).ConfigureAwait(false);
            
            Log.Debug("SendAudioToVoiceChannel: Input and output tasks completed, waiting for FFmpeg to exit");
            
            // Wait for FFmpeg with a timeout using CancellationToken
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await ffmpeg.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                Log.Debug("SendAudioToVoiceChannel: FFmpeg exited with code {ExitCode}", ffmpeg.ExitCode);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("SendAudioToVoiceChannel: FFmpeg did not exit within timeout, killing process");
                ffmpeg.Kill();
            }

            // Wait for stderr reading to complete
            await stderrTask.ConfigureAwait(false);

            // Flush to ensure all data is sent
            Log.Debug("SendAudioToVoiceChannel: Flushing opus stream");
            await opusStream.FlushAsync().ConfigureAwait(false);
            
            Log.Debug("SendAudioToVoiceChannel: Audio transmission complete");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SendAudioToVoiceChannel: Error during audio transmission");
            
            // Try to kill FFmpeg if it's still running
            try
            {
                if (!ffmpeg.HasExited)
                {
                    ffmpeg.Kill();
                }
            }
            catch (Exception cleanupEx)
            {
                Log.Debug(cleanupEx, "SendAudioToVoiceChannel: Error during FFmpeg cleanup");
            }
            
            throw;
        }
        finally
        {
            // Dispose the opus stream if it was created
            if (opusStream != null)
            {
                try
                {
                    await opusStream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeEx)
                {
                    Log.Debug(disposeEx, "SendAudioToVoiceChannel: Error disposing opus stream");
                }
            }
        }
    }

    private static T GetConfiguration<T>(string section) where T : class, new()
    {
        var toReturn = new T();
        var options = new ConfigureOptions<T>(o => _configuration.GetSection(section).Bind(o));
        options.Configure(toReturn);
        return toReturn;
    }

    private static string GetVersionInfo()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "Unknown";
        return version;
    }
}