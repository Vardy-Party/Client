# Calling LocalService `POST /mp` from this app

The VardyParty client does **not** open Chrome. Phone, TV, and desktop call a
LocalService instance on the LAN. MP / FCTV match pages need LocalService **1.1.0+**
(`mp.chrome` on `GET /health`).

Desktop bring-up (Chrome install, headed vs headless, `curl`) lives in the
**m3u8-resolver** repo:

- `docs/MP_CHROME.md`
- `docs/MP_PROTOCOL.md`

## What this branch changes

`LocalLanPlayService` sends MP page URLs to `POST /mp` when the discovered
service advertises `mp.chrome`. Facebook URLs stay on `GET /play/{url}`.

The HTTP timeout for `/mp` is 60 seconds (Chrome launch + chip click). `/play`
keeps the existing M3U8 timeout.

After a 200:

- Use `url` + `requestHeaders` as today.
- Health-check `rewrittenSegments` (not playlist-relative `*.json`).
- `Origin` on the iframe player host is required for those media GETs.

## Local loop on your machine

1. Run LocalService from m3u8-resolver (`dotnet run` on the MP branch).
2. `curl http://127.0.0.1:5019/health` — confirm `mp.chrome`.
3. Run this client on the same LAN (or this PC).
4. Play an MP game from the API. Logs should show `POST /mp`, not `/play`.
