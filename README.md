# Jellyfin Plugin - Radio Online

Automated online radio streaming plugin for Jellyfin that broadcasts audio to Icecast servers with weekly playlist scheduling.

## Version

**v0.0.0.1 Alpha** - Initial release

## Features

- **Icecast Streaming**: Stream audio to any Icecast-compatible server
- **Dual Audio Formats**: Support for OGG (Vorbis) and M4A (AAC) encoding
- **Weekly Schedule Programming**: Assign playlists to specific day/time slots (Monday-Friday)
- **Smart Playlist Management**:
  - Playlist exceeds time slot? Automatically trimmed to fit
  - Playlist shorter than time slot? Remaining time filled with random music
  - Unscheduled hours? Filled with random music from your Jellyfin library
- **Repeat Weekly**: All schedules repeat every week automatically
- **Configuration Dashboard**: Full web-based configuration panel integrated into Jellyfin

## Compatibility

- **Jellyfin**: 10.11.8+
- **.NET**: 9.0+
- **Icecast**: 2.4+ (any Icecast-compatible server)

## Installation

1. Download the latest release DLL from the [Releases](../../releases) page
2. Copy `Jellyfin.Plugin.RadioOnline.dll` to your Jellyfin server's `plugins/` directory
   - Typically located at `/var/lib/jellyfin/plugins/` on Linux
   - Or `C:\ProgramData\Jellyfin\Server\plugins\` on Windows
3. Restart Jellyfin server
4. Navigate to Dashboard → Plugins → Radio Online to configure

## Configuration

### Icecast Server Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Server URL | Full URL to your Icecast server (e.g., `http://your-server:8000`) | - |
| Username | Icecast source username | `source` |
| Password | Icecast source password | - |
| Mount Point | Icecast mount point (e.g., `/radio`) | `/radio` |
| Audio Format | Output format: `ogg` (Vorbis) or `m4a` (AAC) | `ogg` |
| Audio Bitrate | Encoding bitrate in kbps (32-320) | `128` |
| Stream Name | Metadata name for listeners | `Jellyfin Radio Online` |
| Stream Genre | Metadata genre tag | `Various` |
| Public Stream | List in Icecast directory | `false` |

### Jellyfin Settings

| Setting | Description |
|---------|-------------|
| Library User | Jellyfin user account for media library access |
| Enable Radio | Toggle radio automation on/off |

### Schedule Programming

1. Select a **Day** (Monday through Friday)
2. Set **Start Time** and **End Time** in 24-hour format (HH:mm)
3. Choose a **Playlist** from your Jellyfin library (or leave as Random Music)
4. Set an optional **Display Name** for the schedule entry
5. Click **Save Entry**
6. Click **Save Configuration** to apply

## How It Works

1. The plugin runs as a background service on the Jellyfin server
2. It continuously checks the weekly schedule for the current time
3. When a scheduled slot is active, it plays the assigned playlist through FFmpeg → Icecast
4. If the playlist exceeds the time slot, playback is trimmed
5. If the playlist ends early, random music fills the remaining time
6. Unscheduled hours and weekends are filled with random music from the library
7. All schedules repeat weekly

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   Jellyfin Server                        │
│                                                          │
│  ┌──────────────┐  ┌──────────────────┐                 │
│  │  Config Page  │  │  Schedule Manager │                 │
│  │  (Dashboard)  │  │  Service          │                 │
│  └──────┬───────┘  └────────┬─────────┘                 │
│         │                   │                             │
│         ▼                   ▼                             │
│  ┌──────────────┐  ┌──────────────────┐                 │
│  │   Plugin     │  │  Audio Provider   │                 │
│  │   Config     │  │  Service          │                 │
│  └──────┬───────┘  └────────┬─────────┘                 │
│         │                   │                             │
│         ▼                   ▼                             │
│  ┌──────────────────────────────────┐                   │
│  │   Radio Streaming Hosted Service  │                   │
│  │   (Background Service Loop)       │                   │
│  └──────────────┬───────────────────┘                   │
│                 │                                         │
│                 ▼                                         │
│  ┌──────────────────────────────────┐                   │
│  │   Icecast Streaming Service       │                   │
│  │   (FFmpeg → Icecast)              │                   │
│  └──────────────┬───────────────────┘                   │
└─────────────────┼───────────────────────────────────────┘
                  │
                  ▼
         ┌────────────────┐
         │  Icecast Server │
         │  (Streaming)    │
         └────────────────┘
```

## Project Structure

```
jellyfin-plugin-radio-online/
├── Jellyfin.Plugin.RadioOnline/
│   ├── Configuration/
│   │   ├── PluginConfiguration.cs    # Config model
│   │   ├── ScheduleEntry.cs          # Schedule entry model
│   │   ├── configPage.html           # Dashboard UI
│   │   └── configPage.js             # Dashboard JS
│   ├── Services/
│   │   ├── IcecastStreamingService.cs    # FFmpeg → Icecast
│   │   ├── ScheduleManagerService.cs     # Schedule logic
│   │   ├── AudioProviderService.cs       # Library access
│   │   └── RadioStreamingHostedService.cs # Main loop
│   ├── ScheduledTasks/
│   │   └── RadioSchedulerTask.cs     # Dashboard task
│   ├── Api/
│   │   └── RadioOnlineController.cs  # REST API
│   ├── Plugin.cs                     # Plugin entry point
│   ├── ServiceRegistrator.cs         # DI registration
│   └── Jellyfin.Plugin.RadioOnline.csproj
├── Directory.Build.props
├── build.yaml
├── .gitignore
└── README.md
```

## Building from Source

```bash
# Prerequisites: .NET 9 SDK

# Clone the repository
git clone https://github.com/your-username/jellyfin-plugin-radio-online.git
cd jellyfin-plugin-radio-online

# Restore dependencies
dotnet restore

# Build
dotnet build --configuration Release

# The output DLL will be at:
# Jellyfin.Plugin.RadioOnline/bin/Release/net9.0/Jellyfin.Plugin.RadioOnline.dll
```

## Requirements

- A running **Icecast server** configured to accept source connections
- **FFmpeg** installed on the Jellyfin server (Jellyfin includes this by default)
- Audio files accessible in your Jellyfin media library

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
