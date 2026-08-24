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

## Quick start

After publishing, initialize the SDE and check the EVE server:

```powershell
./artifacts/eden/eden.exe status
./artifacts/eden/eden.exe sde update
./artifacts/eden/eden.exe name search "Hobgoblin II"
```

Authorize and synchronize a character:

```powershell
./artifacts/eden/eden.exe character add
./artifacts/eden/eden.exe character list
./artifacts/eden/eden.exe character sync Irene
./artifacts/eden/eden.exe character show Irene wallet
./artifacts/eden/eden.exe character query Irene assets --type-id 2456
```

Tracked characters can be referenced by ID, full name, or an unambiguous first name. Character and corporation queries read the synchronized SQLite cache; run `character sync` again when you want current data.

Query markets and station-trading information:

```powershell
./artifacts/eden/eden.exe market depth "Hobgoblin II" --hub Jita
./artifacts/eden/eden.exe market history "Hobgoblin II" --region Jita --days 30
./artifacts/eden/eden.exe market focus "Hobgoblin II" --hub Jita --character Irene
./artifacts/eden/eden.exe market candidates --hub Jita --capital 300000000
```

For MCP use, publish the server and register the single consolidated executable:

```powershell
codex mcp add eden -- "C:\path\to\EdenToolkit\artifacts\eden-mcp\eden-mcp.exe"
codex mcp get eden
```

Restart the MCP client after adding or replacing the server. You can then ask questions such as “Where should I sell Irene's Hobgoblin II stack?” or “Is this Jita spread realistically tradeable?” The client may call Eden's focused and derived tools automatically.

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

## Market intelligence

Install the SDE once so item names can be resolved, then request compact hub quotes:

```powershell
eden sde update
eden market quote "Hobgoblin II" --hub Hek
eden market quote 2456 --hub Jita --days 30
eden market quote "Hobgoblin II" --hub Dodixie
eden market quote "Hobgoblin II" --hub Amarr
eden market compare "Hobgoblin II" --hubs Hek,Jita,Dodixie,Amarr
```

Quotes contain distinct best buy, best sell, depth buy, and depth sell values. Depth prices are volume-weighted across the best 5% of the station's relevant buy or sell volume. Regional daily history is retained in SQLite and summarized for the requested period. Raw public order books remain in the HTTP cache only and are not persisted in SQLite.

Value previously synchronized character assets with an explicit market meaning:

```powershell
eden inventory value <character-id> --hub Hek --valuation depth-buy
eden inventory value <character-id> --hub Jita --valuation depth-sell
eden inventory value <character-id> --hub Hek --location-id 60005686
```

The result reports immediate liquidation value, replacement value, the selected valuation, and per-type values. A character ID may be omitted when exactly one character is tracked.

### Optional Adam4EVE analytics

Adam4EVE can enrich focused station-trading results with estimated station execution, regional bid/ask history, and regional 5% price percentiles. ESI remains authoritative for current orders, character data, and live market depth.

Adam4EVE requires an identifiable User-Agent containing a contact method. Enable it for the current PowerShell session with:

```powershell
$env:EDEN_ADAM4EVE_ENABLED = "true"
$env:EDEN_ADAM4EVE_USER_AGENT = "EdenToolkit/0.1 contact: you@example.com"
```

Replace the example address with a real monitored contact method. Optional settings include:

```powershell
$env:EDEN_ADAM4EVE_MIN_INTERVAL_SECONDS = "5"
$env:EDEN_ADAM4EVE_TARGET_PARTICIPATION = "0.05"
```

Eden centrally enforces the request interval and caches responses in SQLite. The first uncached focused query can therefore take several seconds. When Adam4EVE is disabled, unavailable, rate-limited, malformed, or lacks coverage, Eden continues with ESI-only analysis and reports that enrichment is unavailable.

#### Adam4EVE disclaimer

Adam4EVE is an independent third-party service and is not operated or controlled by EdenToolkit. Its tracker values are estimates inferred from periodically sampled order-book changes; they are not exact CCP transaction records and can be less accurate in fast-moving markets. Adam4EVE regional history and percentile data also have different semantics from ESI regional trade history and live ESI order-book depth. Eden preserves those distinctions and does not replace authoritative live ESI prices with Adam4EVE values.

Availability, coverage, schemas, limits, and freshness can change without notice. Cached or estimated analytics may be incomplete, delayed, or incorrect. Market calculations and MCP recommendations are informational only: verify important prices and orders in the EVE client and make trading decisions at your own risk. Use of Adam4EVE remains subject to its operator's terms, policies, rate limits, and User-Agent requirements.

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
eden character show <character-id> transactions
eden character show <character-id> jobs
eden character show <character-id> journal
eden character show <character-id> orders
eden character show <character-id> order-history
eden character query <character-id> assets --type-id 34 --limit 100
eden character query <character-id> assets --location-id 60003760
eden character query <character-id> skills --min-level 5
eden character query <character-id> transactions --side buy --type-id 34
eden character query <character-id> transactions --from 2026-08-01 --to 2026-08-31
eden character query <character-id> jobs --status delivered --type-id 165
eden character remove <character-id>
eden corporation show <corporation-name-or-id> blueprints
eden production capacity <corporation-name-or-id> "Hobgoblin II"
eden fittings <character-name-or-id> [--query <fitting-or-hull-name>]
```

Synced character data is stored in `%LOCALAPPDATA%/EdenToolkit/characters.db`. Location and wallet responses are stored as complete JSON values. Assets and skills are transactionally decomposed into indexed SQLite rows while retaining each complete ESI object as raw JSON. Wallet transactions, journal entries, industry jobs, and own market orders are keyed by their permanent ESI IDs and upserted, preserving previously synchronized history. Commands read the committed database state rather than returning the live response directly.

The authorization requests `esi-location.read_location.v1`, `esi-assets.read_assets.v1`, `esi-wallet.read_character_wallet.v1`, `esi-skills.read_skills.v1`, `esi-industry.read_character_jobs.v1`, and `esi-markets.read_character_orders.v1`. JWT signatures, issuers, audiences, expiration, character subjects, and granted scopes are validated. Refresh-token rotation is honored. On Windows, refresh tokens are encrypted with DPAPI for the current OS user; on other systems the character file is restricted to the current user but relies on filesystem protection.

Characters authorized before the industry-jobs scope was added must be authorized again with `eden character add`; selecting the same character replaces its stored grant.

The distributed CLI uses EdenToolkit's registered public client ID by default. Forks and alternate registrations can override it with `--client-id` or `EDEN_EVE_CLIENT_ID`. PKCE does not embed or require a client secret.

## MCP

After publishing, add a stdio server using `artifacts/eden-mcp/eden-mcp.exe`. It exposes:

- `eve_esi_get`
- `eve_server_status`
- `eve_sde_update`
- `eve_sde_status`
- `eve_name_by_id`
- `eve_search_names`
- `eve_sync_character`
- `eve_character_data`
- `eve_list_corporations`
- `eve_sync_corporation`
- `eve_corporation_data`
- `eve_manufacturing_recipe`
- `eve_production_capacity`
- `eve_pi_data`
- `eve_character_fittings`
- `eve_fitting_type_details`
- `eve_market_quote`
- `eve_market_depth`
- `eve_market_history`
- `eve_compare_market_hubs`
- `eve_trading_position`
- `eve_order_state`
- `eve_station_trade_candidates`
- `eve_station_trade_state`
- `eve_value_inventory`

All MCP logging goes to stderr so stdout remains a clean protocol stream.

## Data sources

- ESI: `https://esi.evetech.net/`
- Official SDE JSONL archive: `https://developers.eveonline.com/static-data/eve-online-static-data-latest-jsonl.zip`
- Adam4EVE API (optional analytics): `https://api.adam4eve.eu/`
