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

---
Task ID: 1
Agent: Main Agent
Task: Fix playlist selection error, delete v0.0.0.11, create v0.0.0.8 release

Work Log:
- Analyzed config.html to find root cause: `apiGet()` function used raw `fetch()` instead of Jellyfin's native `ApiClient.ajax()` for API calls
- Replaced `fetch()` with `ApiClient.ajax({type:'GET', url: ApiClient.getUrl(path), dataType:'json'})` for reliable authentication
- Added visible error messages when playlists fail to load (showPlaylistError/hidePlaylistError)
- Added playlist error div in HTML form
- Proper state reset in init() function (configLoaded, playlistsLoaded, playlists, cfg)
- Updated version from 0.0.0.11 to 0.0.0.8 in csproj (Version, AssemblyVersion, FileVersion)
- Updated manifest.json with v0.0.0.8 entry, removed v0.0.0.11
- Built plugin successfully with dotnet publish
- Created ZIP: jellyfin-plugin-radio-online_0.0.0.8.zip (MD5: 8515a821b554d7445f8be714b684f1eb)
- Deleted GitHub release v0.0.0.11 (ID: 313642745) - HTTP 204
- Committed and pushed code to GitHub
- Created GitHub release v0.0.0.8 (ID: 313644459) - NOT pre-release
- Uploaded ZIP asset to release

Stage Summary:
- Fixed playlist selection by replacing raw fetch with ApiClient.ajax
- v0.0.0.11 deleted from GitHub
- v0.0.0.8 released (not pre-release) with ZIP attached
- Download URL: https://github.com/pepebarrascout/jellyfin-plugin-radio-online/releases/download/v0.0.0.8/jellyfin-plugin-radio-online_0.0.0.8.zip
