# Calling LocalService from this app (v1 / v2)

The VardyParty client does **not** open Chrome. Phone, TV, and desktop call a
LocalService instance on the LAN.

**Strategy is catalog-driven** — use each stream’s `resolutionStrategy` / `source`
(`v2`/`mp` → `POST /mp`, `v1`/`fb` → `GET /play`). Do not sniff page hosts.

Shared portable packages (from **Strategies**, GitHub Packages):

| PackageId | Strategy | Role |
| --- | --- | --- |
| `VardyParty.LocalService.Abstractions` | — | `IPlaybackTransportPlugin`, chip normalizer contracts |
| `VardyParty.LocalService.V1` | v1 (= fb) | `AddV1TransportPlugin`, play endpoint id |
| `VardyParty.LocalService.V2` | v2 (= mp) | `AddV2TransportPlugin`, CTU rewrite, chip labels |

Do **not** PackageReference scrape packages from Client.

LocalService host (M3U8-resolver) registers scrape plugins separately.

Desktop bring-up docs in m3u8-resolver: `docs/MP_CHROME.md`, `docs/MP_PROTOCOL.md`.

## Playback path (v2)

1. `AddVardyParty` registers `AddV1TransportPlugin` / `AddV2TransportPlugin`.
2. `LocalLanPlayService` selects a transport plugin via `Matches`, then uses
   `LocalServiceEndpoint` (`mp` vs `play`). `mp` still requires LocalService
   capability `mp.chrome`.
3. Client calls `POST /mp` with page URL (+ optional chip) when strategy is v2 and `mp.chrome` is advertised.
4. LocalService returns playlist `url` + `requestHeaders` (+ optional `rewrittenSegments` from live capture).
5. **Playlist CTU rewrite stays server-side on `/mp` today** — the client resolve
   path has no playlist body, so `PostProcessPlaylist` is not applied client-side yet.
6. Health-check prefers `rewrittenSegments` / rewritten absolute URLs (not playlist-relative `*.json`).

`/mp` timeout is 90s; `/play` keeps the existing M3U8 timeout. Same-match `/mp` calls stay sequential.

## Packages

Client consumes Abstractions + V1 + V2 like `LocalService.Client` (GitHub Packages).
`NuGet.config` points at `https://nuget.pkg.github.com/Vardy-Party/index.json`.

Local fallback until a version is on the feed:

```bash
dotnet pack ../Strategies/VardyParty.LocalService.V1/VardyParty.LocalService.V1.csproj -c Release -o ../Strategies/artifacts/nuget
dotnet pack ../Strategies/VardyParty.LocalService.V2/VardyParty.LocalService.V2.csproj -c Release -o ../Strategies/artifacts/nuget
dotnet restore VardyParty.Streaming/VardyParty.Streaming.csproj --configfile NuGet.LocalShared.config
```
