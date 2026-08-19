# EdenToolkit

`eden` is a C# command-line companion for EVE Online. It queries public ESI endpoints, honors CCP cache headers, keeps a disk cache with stale-on-network-failure behavior, and downloads the official JSONL Static Data Export (SDE) to resolve IDs into names. `eden-mcp` exposes the same services to Codex and other MCP clients over stdio.

## Build and run

Requires the .NET 10 SDK.

```powershell
dotnet build EdenToolkit.slnx
dotnet run --project src/EdenToolkit.Cli -- status
dotnet run --project src/EdenToolkit.Cli -- sde update
dotnet run --project src/EdenToolkit.Cli -- name search Raven
```

Publish a platform-specific `eden.exe`:

```powershell
dotnet publish src/EdenToolkit.Cli -c Release -o artifacts/eden
dotnet publish src/EdenToolkit.Mcp -c Release -o artifacts/eden-mcp
```

## CLI

```text
eden status
eden esi get <relative-path-and-query> [--refresh]
eden sde update [--force]
eden sde status
eden name id <id>
eden name search <text> [--limit <1-100>]
```

The default cache is `%LOCALAPPDATA%/EdenToolkit`. Override it with `EDEN_CACHE_DIR`. Set `EDEN_USER_AGENT` to an identifying ESI User-Agent before distributing or operating the app as a service.

Only public ESI GET endpoints are currently supported. EVE SSO and character-scoped endpoints are intentionally left for a subsequent slice.

## MCP

After publishing, add a stdio server using `artifacts/eden-mcp/eden-mcp.exe`. It exposes:

- `eve_esi_get`
- `eve_server_status`
- `eve_sde_update`
- `eve_sde_status`
- `eve_name_by_id`
- `eve_search_names`

All MCP logging goes to stderr so stdout remains a clean protocol stream.

## Data sources

- ESI: `https://esi.evetech.net/`
- Official SDE JSONL archive: `https://developers.eveonline.com/static-data/eve-online-static-data-latest-jsonl.zip`
