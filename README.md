# Voiceless

A Discord bot that provides text-to-speech (TTS) functionality for Discord voice channels. It monitors text messages from specific users in configured channels and converts them to speech using either OpenAI or ElevenLabs APIs, then plays the audio in voice channels.

## Prerequisites

- .NET 10.0 SDK or later
- FFmpeg (required at runtime for audio conversion)
- Opus native library (required for voice encoding)
- Discord Bot Token
- OpenAI API key (required) or ElevenLabs API key (optional)

## Installing Native Dependencies

### Opus Library

NetCord uses the Opus audio codec for voice transmission. You must install the Opus native library for your platform.

#### Windows 11

1. Download or build the Opus library:
   
   **Option A: Using vcpkg (Recommended)**
   ```powershell
   # Install vcpkg if you haven't already
   git clone https://github.com/Microsoft/vcpkg.git
   cd vcpkg
   .\bootstrap-vcpkg.bat
   
   # Install opus
   .\vcpkg install opus:x64-windows
   
   # Copy the DLL to your application directory
   copy ".\installed\x64-windows\bin\opus.dll" "C:\path\to\your\app\bin\Debug\net9.0\"
   ```

   **Option B: Download pre-built binaries**
   - Search for "opus.dll Windows download" and download from a trusted source
   - Or build from source: https://opus-codec.org/downloads/
   - Copy `opus.dll` to your application's output directory (e.g., `bin\Debug\net9.0\` or `bin\Release\net9.0\`)

#### Debian/Ubuntu Linux

```bash
# Install libopus
sudo apt-get update
sudo apt-get install libopus0 libopus-dev

# Verify installation
ldconfig -p | grep opus
```

The library should be installed system-wide and will be found automatically.

#### Other Linux Distributions

**Fedora/RHEL/CentOS:**
```bash
sudo dnf install opus opus-devel
```

**Arch Linux:**
```bash
sudo pacman -S opus
```

### FFmpeg

FFmpeg is required for audio format conversion.

#### Windows 11

1. Download FFmpeg from https://ffmpeg.org/download.html (get the "release builds")
2. Extract the archive
3. Add the `bin` folder to your system PATH, or copy `ffmpeg.exe` to your application's output directory

**Using Chocolatey:**
```powershell
choco install ffmpeg
```

**Using winget:**
```powershell
winget install ffmpeg
```

#### Debian/Ubuntu Linux

```bash
sudo apt-get update
sudo apt-get install ffmpeg
```

#### Other Linux Distributions

**Fedora/RHEL/CentOS:**
```bash
sudo dnf install ffmpeg
```

**Arch Linux:**
```bash
sudo pacman -S ffmpeg
```

## Building

```bash
# Restore NuGet packages
dotnet restore

# Build the solution
dotnet build

# Build for Release
dotnet build -c Release
```

## Versioning

This project uses [MinVer](https://github.com/adamralph/minver) for automatic versioning based on Git tags. Versions are calculated automatically from the most recent semver tag (e.g., `v1.0.0`).

- To create a new release, push a Git tag following semantic versioning (e.g., `v1.2.3`)
- The version is displayed at startup in the console logs
- Pre-release versions include the commit height from the last tag

## Configuration

Copy `appsettings.json` to your output directory and configure:

```json
{
  "discord": {
    "token": "YOUR_DISCORD_BOT_TOKEN"
  },
  "openai": {
    "token": "YOUR_OPENAI_API_KEY",
    "model": "gpt-4o-mini",
    "voice": "alloy"
  },
  "elevenlabs": {
    "token": "YOUR_ELEVENLABS_API_KEY",
    "voiceId": "YOUR_VOICE_ID"
  },
  "target": {
    "users": [
      {
        "userId": 123456789,
        "voice": "User's Voice Name",
        "servers": [
          {
            "serverId": 987654321,
            "channels": [111222333, 444555666]
          }
        ]
      }
    ]
  }
}
```

> **Note:** The `elevenlabs` section is optional. If not configured, OpenAI will be used for TTS synthesis.
```

You can also use .NET User Secrets for sensitive configuration:

```bash
dotnet user-secrets set "discord:token" "YOUR_TOKEN"
dotnet user-secrets set "openai:token" "YOUR_TOKEN"
```

## Running

```bash
dotnet run --project Voiceless
```

## Deployment

### Publishing for Linux

```bash
dotnet publish Voiceless -o publish/linux-x64/Voiceless/ -r "linux-x64" --self-contained false -c Release --nologo
```

Don't forget to install `libopus0` and `ffmpeg` on the target Linux system!

### Publishing for Windows

```bash
dotnet publish Voiceless -o publish/win-x64/Voiceless/ -r "win-x64" --self-contained false -c Release --nologo
```

Ensure `opus.dll` and `ffmpeg.exe` are in the published directory or in the system PATH.

## Troubleshooting

### "Unable to load DLL 'opus'"

This error means the Opus native library is not found. Follow the installation instructions above for your platform.

### No audio heard in voice channel

1. Ensure FFmpeg is installed and accessible
2. Check that the bot has permission to speak in the voice channel
3. Verify the Opus library is properly installed
4. Check the console logs for any error messages

## License

See LICENSE file for details.
