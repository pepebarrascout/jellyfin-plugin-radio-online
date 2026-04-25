---
Task ID: 1
Agent: Main Agent
Task: Fix v0.0.0.8 Liquidsoap integration - not transmitting to Icecast

Work Log:
- Identified root cause: Jellyfin runs in Docker container, Liquidsoap installed on host server
- Liquidsoap binary does not exist inside the Docker container
- Audio file paths inside container don't match host paths
- /tmp/radio-online/ directory is inside container, not accessible by host Liquidsoap
- Solution: Make FFmpeg the default streaming engine (bundled with Jellyfin Docker image)
- Added dual engine support: FFmpeg (default) and Liquidsoap (optional) via config
- Reduced logging: removed repetitive ScheduleManager logs every 5s, removed config validation spam
- Updated config.html with streaming engine selector and engine status in status tab
- Updated RadioOnlineController to show active engine
- Updated ServiceRegistrator to register both IcecastStreamingService and LiquidsoapStreamingService
- Rewrote RadioStreamingHostedService to support both engines with clean switching
- Built successfully with 0 warnings/errors
- Created v0.0.0.10-alpha release and updated manifest

Stage Summary:
- Release: v0.0.0.10-alpha published at https://github.com/pepebarrascout/jellyfin-plugin-radio-online/releases/tag/v0.0.0.10-alpha
- Key changes: FFmpeg default engine (Docker compatible), dual engine support, reduced log spam
- Files changed: 7 files (PluginConfiguration, ServiceRegistrator, RadioStreamingHostedService, RadioOnlineController, ScheduleManagerService, config.html, csproj)
