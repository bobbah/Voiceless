# Copilot Instructions for Voiceless

## Repository Overview

**Voiceless** is a C# Discord bot that provides text-to-speech (TTS) functionality for Discord voice channels. It monitors text messages from specific users in configured channels and converts them to speech using either OpenAI or ElevenLabs APIs, then plays the audio in voice channels. The bot uses DSharpPlus for Discord integration and requires FFmpeg for audio processing.

- **Language**: C# 13 with .NET 9.0
- **Type**: Console application (Discord bot)
- **Size**: Small (~15 source files)
- **External Dependencies**: OpenAI API, ElevenLabs API (optional), Discord Bot Token, FFmpeg (runtime)

## Build Instructions

### Prerequisites
- .NET 9.0 SDK or later
- FFmpeg (required at runtime for audio conversion)

### Build Commands

**Always run commands from the repository root** (`/path/to/Voiceless/`).

```bash
# Restore NuGet packages (run first, required before build)
dotnet restore

# Build the solution (Debug configuration)
dotnet build

# Build for Release
dotnet build -c Release

# Publish for Linux x64
dotnet publish Voiceless -o publish/linux-x64/Voiceless/ -r "linux-x64" --self-contained false -c Release --nologo

# Publish for Windows x64
dotnet publish Voiceless -o publish/win-x64/Voiceless/ -r "win-x64" --self-contained false -c Release --nologo
```

### Build Notes
- The build produces nullable reference warnings (CS8618, CS8629, CS8604). These are expected and do not cause build failure.
- One deprecation warning (CS0618) exists in `ElevenLabsVoiceSynthesizer.cs` - this is known and non-blocking.
- Build time is typically under 10 seconds.

### No Tests
This project currently has no test projects or test infrastructure. `dotnet test` will find no tests to run.

## Project Layout

```
Voiceless/
├── .github/
│   └── workflows/
│       └── release.yml          # CI: Builds and publishes releases on tags
├── Voiceless/                   # Main project directory
│   ├── Configuration/           # Configuration POCO classes
│   │   ├── DiscordConfiguration.cs
│   │   ├── ElevenLabsConfiguration.cs
│   │   ├── MiscConfiguration.cs
│   │   ├── OpenAIConfiguration.cs
│   │   ├── TargetConfiguration.cs
│   │   ├── TargetServer.cs
│   │   └── TargetUser.cs
│   ├── Data/
│   │   └── QueuedMessage.cs     # Message queue record
│   ├── Voice/                   # TTS synthesizer implementations
│   │   ├── IVoiceSynthesizer.cs # Interface for TTS providers
│   │   ├── ElevenLabsVoiceSynthesizer.cs
│   │   └── OpenAIVoiceSynthesizer.cs
│   ├── Program.cs               # Main entry point and Discord event handlers
│   ├── Voiceless.csproj         # Project file (net9.0)
│   └── appsettings.json         # Configuration template
├── Voiceless.sln                # Solution file
└── .gitignore                   # Standard Visual Studio gitignore
```

## Key Files

| File | Purpose |
|------|---------|
| `Voiceless/Program.cs` | Main entry point, Discord client setup, event handlers |
| `Voiceless/Voiceless.csproj` | Project file with dependencies and target framework |
| `Voiceless/appsettings.json` | Configuration file for API tokens and settings |
| `.github/workflows/release.yml` | GitHub Actions workflow for releases |

## Dependencies (NuGet Packages)

- `DSharpPlus.VoiceNext` (5.0.0-nightly-02560) - Discord voice support
- `ElevenLabs-DotNet` (3.7.0) - ElevenLabs TTS API
- `OpenAI` (2.7.0) - OpenAI TTS API
- `Microsoft.Extensions.Configuration.*` (10.0.0) - Configuration binding

## CI/CD Pipeline

The `release.yml` workflow runs **only on tag pushes** (not on regular commits):
- Builds and publishes for `linux-x64` and `win-x64`
- Creates a GitHub release with the artifacts
- Uses .NET 9.0.x and runs on `ubuntu-22.04`

**There is no CI workflow for pull requests.** Changes are validated locally.

## Configuration

The application reads from `appsettings.json` and optionally user secrets. Key configuration sections:

- `discord.token` - Discord bot token (required)
- `openai.token` - OpenAI API key (required)
- `elevenlabs.token` - ElevenLabs API key (optional, uses OpenAI if not set)
- `target.users` - List of Discord users to monitor

## Coding Conventions

- **Nullable reference types** are enabled (`<Nullable>enable</Nullable>`)
- **Implicit usings** are enabled (`<ImplicitUsings>enable</ImplicitUsings>`)
- Uses C# 13 features including generated regex (`[GeneratedRegex(...)]`)
- Configuration classes are POCOs bound via `IConfiguration`
- Voice synthesizers implement `IVoiceSynthesizer` interface

## Running the Application

The bot requires:
1. A configured `appsettings.json` with valid API tokens
2. FFmpeg installed and available in PATH (for audio conversion)
3. A Discord bot with appropriate permissions (Voice, Message Content intents)

```bash
# Run after building
dotnet run --project Voiceless
```

## Trust These Instructions

These instructions have been validated against the actual repository state. Only perform additional exploration if the information here is incomplete or found to be in error.
