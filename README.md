# Srv TV for Windows

Native Windows port of **Srv TV** (the Android TV app): same channels, same
favorites, same guide — WPF on .NET 8 with LibVLCSharp (VLC) for playback.

Keyboard map mirrors the TV remote: arrows = D-pad, Enter = OK, Esc = Back.

## Build & run

Requires .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

```powershell
dotnet build src/SrvTv/SrvTv.csproj
dotnet run --project src/SrvTv/SrvTv.csproj
```

Publish a single folder (`publish/`):

```powershell
dotnet publish src/SrvTv/SrvTv.csproj -c Release -o publish
```

## Bring your TV favorites over

Channel IDs are computed exactly like the Android app, so favorites carry
over 1:1. On first run the app offers to import the favorites JSON array
(e.g. paste the `favorites` value from the Android app's `iptv_prefs.xml`).

## Status

- [x] Data layer: models, M3U parser, channel repository (playlists,
      filters, categories, favorites + grouping, watch recency, probe
      protection, JSON cache), stream prober, XMLTV guide repository
- [ ] Grid + category rail UI
- [ ] Player + PiP + guide + schedules + search + settings
