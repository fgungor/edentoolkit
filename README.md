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

The generic `esi get` command remains limited to public endpoints; authenticated access is deliberately exposed through the character tracking commands below.

## Track characters

Register a native application in the EVE Developers Portal and add this exact callback URL:

```text
http://localhost:52731/callback/
```

Authorize characters using OAuth Authorization Code with PKCE; no client secret is required:

```powershell
eden character add
eden character list
eden character sync all
eden character show <character-id> location
eden character show <character-id> assets
eden character show <character-id> wallet
eden character show <character-id> skills
eden character remove <character-id>
```

The authorization requests only `esi-location.read_location.v1`, `esi-assets.read_assets.v1`, `esi-wallet.read_character_wallet.v1`, and `esi-skills.read_skills.v1`. JWT signatures, issuers, audiences, expiration, character subjects, and granted scopes are validated. Refresh-token rotation is honored. On Windows, refresh tokens are encrypted with DPAPI for the current OS user; on other systems the character file is restricted to the current user but relies on filesystem protection.

The distributed CLI uses EdenToolkit's registered public client ID by default. Forks and alternate registrations can override it with `--client-id` or `EDEN_EVE_CLIENT_ID`. PKCE does not embed or require a client secret.

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
