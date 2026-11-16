# JellyEncore

Encore is a small .NET 10 service that runs alongside a Jellyfin 12.4 server. When a listed user's music queue is down to one song after the current one, Encore adds more songs to the queue, so the music keeps going. v0.1 uses only songs already in the library. v0.2 can also fetch songs the server does not have from Soulseek (through slskd), import them into the library and queue them; it is off by default.

## What it does

When an allow-listed user's queue is down to one song after the current one (the current song is the second-to-last), Encore adds `ENCORE_BATCH` (2) owned songs to the end of that session's queue with PlayLast. When only the last song Encore added is still to come, it adds the next batch (a top-up), so a song is always left to play and the radio keeps going for up to about 60 songs per run. If the app's next queue report does not show the songs, Encore sends them once more (only while something is playing, and never just because a request got no answer). If the second-to-last song was missed (a restart, a skip straight to the last song), the last song still starts the radio. Songs come from similar artists, similar songs and an instant mix, and never repeat within the recent window.

Works with Jellyfin Web and Finamp. Other apps are skipped (reason `unknown_client`). Nothing is added while a session has repeat on (`repeat_on`) or does not allow remote control (`no_remote_control`).

## Requirements

- Jellyfin 12.4. The queue rules depend on how 12.4 stores the queue.
- A Jellyfin API key made by an admin (Dashboard, API Keys). Encore needs it to see all sessions and to send PlayLast. Enter it yourself in the environment file; never put it on a command line or in the repository.
- The playing app must allow remote control, and repeat must be off.

## Layout

- `src/Encore` - the host: config, Jellyfin client, session watcher, trigger, picker, orchestrator, store, health, and the v0.2 fetch side (slskd client, Deezer and ListenBrainz discovery, scorer, importer, fetch worker)
- `tests/Encore.Tests` - xUnit tests. `EndToEnd/` runs the orchestrator against a fake Jellyfin and a real SQLite file.

## Build, test, run

    dotnet build
    dotnet test
    dotnet run --project src/Encore

## Environment variables

Standard ASP.NET Core variables are supported.

| Variable | Purpose | Default |
|---|---|---|
| `ASPNETCORE_URLS` | Address(es) the host listens on | `http://localhost:5000` |
| `ASPNETCORE_HTTP_PORTS` | Port(s) the host listens on (the image uses 8080) | - |
| `ASPNETCORE_ENVIRONMENT` | Environment name (`Development`, `Production`) | `Production` |
| `Logging__LogLevel__Default` | Minimum log level | `Information` |

### Encore settings

Invalid settings are all reported at once (one JSON log line per bad variable) and the process exits with code 2. A database that is broken or newer than this build exits with code 3. The API key is never logged.

| Variable | Purpose | Default |
|---|---|---|
| `JELLYFIN_URL` | Jellyfin base URL (http/https), required | - |
| `JELLYFIN_API_KEY` | Jellyfin API key, required, no whitespace | - |
| `ENCORE_ALLOWED_USERS` | Comma-separated Jellyfin user names or user ids, at least one. Only these users get radio | - |
| `ENCORE_LOOKAHEAD` | Songs planned ahead (1-20) | `5` |
| `ENCORE_BATCH` | Songs sent per trigger (1 to lookahead) | `2` |
| `ENCORE_POLL_SECONDS` | Fallback poll interval while the socket is down (5-300) | `10` |
| `ENCORE_RECENT_WINDOW` | `session` or a duration `30m`, `6h`, `2d` (1m-30d). Songs played or given in this window are not picked again | `session` |
| `ENCORE_DRY_RUN` | `true/false/1/0/yes/no`. **Defaults to on**: a fresh install only logs what it would add and sends nothing | `true` |
| `ENCORE_DATA_DIR` | Writable folder for `encore.db` and `device-id` | `/data` |
| `LOG_LEVEL` | Trace, Debug, Information, Warning, Error (Encore category) | `Information` |

#### Fetching (v0.2)

Fetching is off unless `ENCORE_FETCH_MODE` says otherwise. In mode `off` the slskd settings are not read and nothing outside Jellyfin is called.

| Variable | Purpose | Default |
|---|---|---|
| `ENCORE_FETCH_MODE` | `off`, `dry_run` or `live` (case-insensitive; `dry-run` and `dryrun` also work). See "Fetch modes" | `off` |
| `ENCORE_SLSKD_URL` | slskd base URL, required unless off. Absolute http or https, no user info, no query. Example `http://gluetun-slskd:5030` | - |
| `ENCORE_SLSKD_API_KEY` | slskd API key, required unless off. 16-255 characters, no whitespace. Never logged or shown. Use this name, not `SLSKD_API_KEY`: a stack `.env` often already holds slskd's own admin key under that name | - |
| `ENCORE_SLSKD_DOWNLOADS_DIR` | Where Encore sees slskd's downloads folder. In `live` it must exist and be readable; if it is not writable Encore warns (copies stay behind) | `/slskd-downloads` |
| `ENCORE_RADIO_DIR` | Where Encore sees the folder it imports songs into. In `live` it must exist and pass a write test | `/music-radio` |
| `ENCORE_RADIO_JELLYFIN_DIR` | The same folder as Jellyfin sees it. Must start with `/`, no `..` | `/music-radio` |
| `ENCORE_SLSK_SEARCH_LIMIT` | Soulseek search budget `<searches>/<seconds>` (searches 1-100, seconds 10-3600), shared by all users, with at least 5 seconds between searches | `34/220` |
| `ENCORE_FETCH_CONCURRENCY` | Songs searched or downloaded at once (1-5) | `2` |

`ENCORE_FETCH_MODE=live` together with `ENCORE_DRY_RUN=true` is a configuration error (live downloads songs but dry run sends none). `dry_run` works with either value of `ENCORE_DRY_RUN`. There is no setting for a share of new versus owned songs: unowned songs the radio suggests come first and the owned chain fills the rest.

Fixed in v0.2, not configurable: two search queries per song, up to three peers tried per song, a 15-minute deadline per song, a 24-hour memory of songs that could not be found, at most 30 fetches per hour in total, a 20 GB free-space floor on the radio folder, an owned-song index refreshed every 30 minutes, FLAC or MP3 at 320 kbps only, and one hand-out pass-over for a song that is still downloading (it is skipped the second time).

Fixed in v0.1, not configurable: a 60-minute dedupe window, a 20-second cooldown per session, at most 30 songs per session and 120 in total per hour (re-sent songs count), at most 60 songs per radio run, two fire attempts 15 seconds apart, one re-send of songs the next queue report does not show, a 3-second delivery lag (plus the poll interval for poll reports) before a report can prove songs missing, a 10-second grace after a stop, a 10-second timeout per Jellyfin call, song lengths of 60 seconds to 12 minutes, and at most 8 Jellyfin calls per pick.

## Running in Docker

- Image: `ghcr.io/phlurblepoot/jellyencore:<tag>`. Pin a version tag, not `latest`.
- Listens on port 8080. Mount a host folder at `/data`; it holds `encore.db` and `device-id`.
- Runs as a non-root user (for example `user: "99:100"`). `/data` must be writable by that user. For live fetching use `user: "99:100"` (the user slskd runs as on Unraid), see "Mounts for fetching".
- Join the same Docker network as Jellyfin and use Jellyfin's container name in `JELLYFIN_URL`.
- Keep the API key in an `.env` file next to the compose file and reference it from the compose file. Do not write it inline.

## Jellyfin Web fix (Jellyfin 12.4)

Radio needs a live connection (a websocket) between Jellyfin and the player. Jellyfin 12.4 turned off legacy authentication, but the bundled Jellyfin Web client still opens `/socket?api_key=...`, which the server now rejects ("Token is required"). A web session without that socket cannot be remote-controlled, so Encore's PlayLast is not delivered and the web player never shows radio songs. Finamp is not affected.

`contrib/jellyfin-web-socket-fix.js` fixes this in the browser. It renames that one query parameter to `ApiKey`, which 12.4 accepts, and only for `/socket` URLs. It is safe to run twice, keeps 12.4's stricter authentication, and can be removed once Jellyfin Web sends `ApiKey` itself.

Install it with the JavaScript Injector plugin (the plugin that adds custom JavaScript to Jellyfin Web):

1. Install the JavaScript Injector plugin in the Jellyfin dashboard and restart Jellyfin.
2. Open the plugin's settings and add a new script. Name it for example "Encore socket fix" and paste the whole of `contrib/jellyfin-web-socket-fix.js`. Enable it and save.
3. Reload Jellyfin Web (a hard refresh, Ctrl+F5) on every device that should get radio.
4. Check in the browser's developer tools (Network, WS) that the `/socket?ApiKey=...` connection stays open, or look for `queue.confirmed` in Encore's logs after a radio fire.

## Requests from the Jellyfin web search (v0.3)

`contrib/jellyfin-request-search.js` adds a "Not in library? Request" section at the bottom of the Jellyfin Web search page when `ENCORE_REQUESTS=on`. It lists songs and albums Encore found that the library does not have, with a Request button or the request's status.

1. In the JavaScript Injector plugin, import `contrib/jellyfin-request-search.import.json` (or add a script and paste the whole `.js` file), then press Save.
2. Set the two settings at the top of the script: `ENCORE_URL_HTTP` (Encore's http address) and `ENCORE_URL_HTTPS` (leave empty until Encore is reachable over https). On an https Jellyfin page with no https address, the section is only a link to Encore's request page.
3. Hard-refresh Jellyfin Web (Ctrl+F5).

The script sends the Jellyfin token only when Jellyfin Web talks to the same address the page came from. Its tests run with `node --test tests/js`.

## Going live

1. Start with `ENCORE_DRY_RUN=true` (the default).
2. Play an album as an allow-listed user, through to its last song.
3. Look for `queue.would_add` in the logs: one line per album end, at the second-to-last song (the last song logs `already_queued` at Debug). The line lists the songs Encore would have added. Check that they are sensible.
4. Set `ENCORE_DRY_RUN=false` and restart. Encore only queues songs from then on.

## Fetching from Soulseek (v0.2)

### What it does

When the radio plans its next songs, it also asks Deezer (song radio, related artists) and ListenBrainz (similar artists) for suggestions. A suggestion the server already has is queued as the server's own copy, with no download. A suggestion the server does not have is searched on Soulseek through slskd, downloaded (FLAC or MP3 at 320 kbps), tagged, moved into the radio folder, announced to Jellyfin and queued once Jellyfin lists it. The radio never waits for a download: a song that is not ready at hand-out time is passed over once and then skipped, and owned songs fill the batch. Soulseek failures therefore mean more owned songs, not a stall.

### Fetch modes

| Mode | What happens |
|---|---|
| `off` (default) | v0.1 behaviour. No Deezer, ListenBrainz or slskd calls |
| `dry_run` | Plans the pool and searches and scores on Soulseek, but downloads nothing. Logs `pool.planned` and `score.would_download`. Songs sent are the same as in `off` |
| `live` | Downloads, imports and queues fetched songs. Needs `ENCORE_DRY_RUN=false` |

### Mounts for fetching

Add to the Encore service (paths are the QBStream ones):

- `/mnt/user/music-radio:/music-radio` read-write: the folder Encore imports into. Jellyfin must see the same folder (mounted at `/music-radio` in the Jellyfin container) and it must be inside a Jellyfin Music library location (add `/music-radio` to the Music library).
- `/mnt/user/downloads/slskd:/slskd-downloads` read-write: slskd's downloads folder (`/downloads` inside slskd), so Encore can pick up finished files and delete its copy.
- `user: "99:100"` and a `/data` folder writable by 99:100. slskd runs as 99:100, so its folders are only deletable by that user. As another user imports still work, but every slskd copy stays behind (`import.source_left`, and a `/health` warning).
- `ENCORE_SLSKD_URL` pointing at slskd as Encore's container reaches it (behind a VPN container the firewall must let Encore through on slskd's port), and the slskd API key in the `.env` file as `ENCORE_SLSKD_API_KEY`. If slskd limits API keys to a network range (CIDR), include Encore's network.

### Going live safely: dry_run first

1. Set `ENCORE_FETCH_MODE=dry_run`, the slskd URL and key, and restart. Leave `ENCORE_DRY_RUN` as it is.
2. Play music as an allow-listed user for a day or two. Check `/health`: `slskd.reachable` and `slskd.loggedIn` are true.
3. Read `pool.planned` (the songs the pool would use, with `source` and `copy`), then `score.would_download` (peer, file, format, size, estimated time) and `fetch.failed` / `score.none` (why a song had no acceptable copy). Many failures only mean more owned songs, but a pattern (everything fails the same way) is worth a look.
4. Mount the two folders, set the container user, make sure `ENCORE_DRY_RUN=false`, set `ENCORE_FETCH_MODE=live` and restart.
5. Watch for `fetch.ready` and `queue.added` with `soulseek` sources, and for songs appearing under `soulseek/` in the radio folder and in Jellyfin.

Rollback is `ENCORE_FETCH_MODE=off` (or the previous image tag; the database keeps a `.v1.bak` copy from before the v0.2 migration). Songs already imported stay in the library.

## Health

`GET /health` returns JSON. It shows the same kind of data as the startup summary, and it never shows either key.

- `status`: `starting` for the first 30 seconds; `ok` when the socket is subscribed (or polling works), every allow-list name resolved and the store is open; `degraded` otherwise; `stopped` (HTTP 503) when the main loop has not run for 60 seconds. Fetch problems never change the status while owned radio works; they appear under `warnings`.
- `warnings`: a list of strings, empty in mode `off`. Possible entries: `slskd: unreachable`, `slskd: key rejected`, `slskd: not logged in`, `music-radio is not in a Jellyfin music library`, `music-radio not scanned by Jellyfin yet (run Scan All Libraries)`, `music-radio free space below 20 GB`, `owned index not loaded` (5 minutes after start), `slskd copies left behind: N`, `deezer: failing` (a Deezer error in the last 10 minutes).
- `slskd`: `configured`, the URL (no credentials), `reachable`, `loggedIn`, `lastOkUtc`, `lastError`, `searchesInWindow` and `searchLimit` (the budget).
- `fetch`: `mode`, `available` and `unavailableReason` (`off`, `not_configured`, `slskd_unreachable`, `slskd_key_rejected`, `not_logged_in`, `radio_dir_not_in_library`, `disk_low`, `hourly_cap`, `index_not_ready`), `ownedIndex` (`loaded`, `items`, `refreshedUtc`), job counters since start (`queued`, `inFlight`, `ingesting`, `ready`, `failed`, `cancelled`, `wouldDownload`), `passedOver` and `skipped` (picks passed over or skipped at hand-out), `lastHour` against `hourlyCap`, `radioDirInLibrary` and `radioLibrary`, `freeBytes`, `sourceLeftBehind`, `lastReadyUtc` and `lastError`.
- `dryRun`: whether the service only logs.
- `socket`: `state` (`connecting`, `subscribed`, `down`), when it changed, `reconnects`, and the last message and Sessions times. An idle server sends nothing, so an old `lastSessionsUtc` alone is not a fault.
- `poll`: whether the fallback poll is running and when it last worked.
- `allowList`: the configured names, the resolved name-to-id map, names that did not resolve, and the last error.
- `triggers` and `playLast`: counters since start (fires, sent, dry runs, no picks, failures, songs sent, songs never seen in the queue).
- `store`: whether the database is working and its schema version.

The image's HEALTHCHECK runs `--healthcheck`, which calls `/health` and fails on anything but a 2xx.

## Logs

JSON lines on stdout. Set `LOG_LEVEL` to see more. The events an operator looks for:

| Event | Level | Meaning |
|---|---|---|
| `startup.config` | Info | The settings in use, without the key |
| `startup.config_invalid` | Critical | A setting is wrong (exit 2) |
| `store.ready` | Info | The database is open, with the number of rows pruned at startup |
| `store.error` | Error | A database operation failed |
| `allowlist.resolved` | Info | Which names matched which user ids |
| `allowlist.unresolved` | Warning | A name in `ENCORE_ALLOWED_USERS` matches no user |
| `allowlist.resolve_failed` | Warning | Jellyfin could not list users; retried with backoff |
| `jellyfin.auth_failed` | Error | Jellyfin rejected the API key (401 or 403) |
| `socket.closed`, `socket.reconnect_scheduled` | Warning, Info | The session socket dropped and is coming back |
| `trigger.fired` | Info | A radio fire started (`Kind` is `start`, `topup` or `resend`; `Rule` is `near_end`, `last_song` or `resend`) |
| `trigger.skipped` | Info | Radio did not start for a reason (see below). `not_last`, `not_allowed` and `already_queued` are logged at Debug as `trigger.skipped_quiet` |
| `trigger.gave_up` | Warning | Two attempts failed; no more tries for this song |
| `queue.would_add` | Info | Dry run: the songs that would have been queued |
| `queue.added` | Info | Songs sent with PlayLast |
| `queue.add_failed` | Error | PlayLast failed; the reason and status are logged |
| `queue.unconfirmed` | Warning | Songs were accepted but were not in the queue the app reported at the next song or stop. `Resend` says what happens next: `pending` (sent again now), `armed` (sent again if a song starts within 10 s), `skipped_not_playing`, `skipped_user_songs`, `already_resent` |
| `queue.unverified` | Info | The session ended before a queue report could show the songs. Delivery is unknown, not failed; nothing is re-sent |
| `queue.resent` | Info | The missing songs were sent once more, appended to the end of the queue |
| `queue.add_no_reply` | Warning | PlayLast got no answer; not retried, the next queue report decides |
| `picks.planned`, `picks.none` | Info, Warning | Which songs were picked, or that none were found |
| `worker.crashed` | Critical | A step failed; Encore keeps running |
| `pool.planned` | Info | Fetch dry_run or live: the songs the pool picked, each with its `source` and `copy` (server copy or a fetch) |
| `fetch.requested`, `fetch.ready`, `fetch.failed` | Debug, Info, Info | A song was asked for; it is imported and listed by Jellyfin; it failed with a `reason` (`no_results`, `no_acceptable`, `budget`, `slskd_unavailable`, `all_peers_failed`, `deadline`, `cancelled`, `import_failed`, `ingest_timeout`, `restart`, `file_missing`, `internal`) |
| `fetch.unavailable`, `fetch.available` | Warning, Info | Fetching stopped or resumed, with the reason (same names as `fetch.unavailableReason` in `/health`) |
| `score.would_download` | Info | dry_run: the copy that would be downloaded (peer, file, format, size, estimated time) |
| `score.none` | Info | Soulseek had results but no acceptable copy, with counts per rejection reason |
| `slskd.state` | Info | slskd became reachable, unreachable, logged in or logged out |
| `slskd.transfer_stalled` | Warning | A peer was too slow, idle or refused; the next peer is tried |
| `import.done`, `import.failed`, `import.source_left` | Info, Warning, Warning | The file was tagged and moved into the radio folder; the import failed (`mismatch`, `disk_low`, `io`, `tag`); slskd's copy could not be deleted |
| `ingest.ready`, `ingest.timeout`, `ingest.library_check` | Info, Warning, Info or Warning | Jellyfin listed the new file; it did not within 3 minutes; the radio folder is, or is not, inside a Music library |

`trigger.skipped` reasons: `not_allowed`, `not_audio` (a video or episode), `no_remote_control`, `repeat_on`, `unknown_client`, `stale_queue`, `no_history`, `not_last` (two or more songs still to come), `duplicate`, `rate_limited`, `run_cap`, `already_queued` (songs sent at the previous song have not shown up yet; the delivery check handles them).

## Troubleshooting

- **Nothing happens.** Read the `trigger.skipped` reasons. `repeat_on` means repeat is on for that session. `no_remote_control` means the app does not allow remote control. `unknown_client` means the app is not Jellyfin Web or Finamp. `stale_queue` and `no_history` are normal for the first song after a restart or a song started from idle.
- **`jellyfin.auth_failed`, or `/health` shows `unauthorized`.** The key was revoked or mistyped. Create a new key in Jellyfin and update the `.env` file.
- **`allowlist.unresolved`.** A user name is mistyped, or the user does not exist. Use the user's exact name, or the user's id so that a rename does not break it. Each unresolved name is checked again every 5 minutes.
- **Songs never show up.** `queue.unconfirmed` means Jellyfin accepted the command but the songs were not in the queue the app reported next; it is normally followed by `queue.resent`. Two `queue.unconfirmed` in a row for one queue end (the second with `already_resent`) mean the app is not taking commands, and Encore stops for that queue end. Check the app's own queue screen.
- **Fetching: slskd 401 (`slskd.auth_failed`).** The key in `ENCORE_SLSKD_API_KEY` is wrong, or slskd limits that key to a network range (CIDR) that does not include Encore's container. Fix the key or add the range in slskd's settings.
- **Fetching: `slskd: unreachable` in `/health`.** Encore cannot reach `ENCORE_SLSKD_URL`. Check the name and port, that both containers share a network, and, if slskd runs behind a VPN container, that its firewall lets Encore through.
- **Fetching: `slskd: key rejected` in `/health`.** slskd answered but refused the key (HTTP 401 or 403). Check `ENCORE_SLSKD_API_KEY`, that the key exists in slskd's `authentication.api_keys`, and that its CIDR range includes Encore. The `readwrite` role is enough; Encore reads the login state from `/api/v0/application`, which needs no admin role.
- **Fetching: `slskd: not logged in`.** slskd is running but not connected to the Soulseek network (wrong Soulseek account, or the network is down). Check slskd's own page.
- **Fetching: `radio_dir_not_in_library`.** Jellyfin has no Music library location that contains the radio folder, so fetched songs would never appear. Add `/music-radio` (as Jellyfin sees it) to the Music library and check `ENCORE_RADIO_JELLYFIN_DIR`. Encore re-checks every 30 minutes.
- **Fetching: `radio_dir_not_scanned` (fetches fail with this reason).** The radio folder is in the Music library, but Jellyfin has never scanned it (a library location that was empty when it was added is skipped). Run **Scan All Libraries** once in Jellyfin (Dashboard, Libraries). Fetching stays on; Encore makes no scan call itself, because that would scan every library. In `live` mode Encore creates `soulseek` inside the radio folder at startup, so the folder is never empty again.
- **Fetching: copies left behind (`import.source_left`, `slskd copies left behind`).** Encore could not delete slskd's copy of a downloaded file. Run the Encore container as `user: "99:100"` and check the `/slskd-downloads` mount is read-write.
- **Fetching: nothing is ever fetched.** Look at `fetch.unavailableReason` in `/health`: `hourly_cap` (30 fetches per hour reached), `disk_low` (under 20 GB free in the radio folder), `index_not_ready` (the owned-song index is still loading) or one of the slskd reasons above. In `dry_run` nothing is downloaded by design. If `fetch.failed` shows `no_acceptable` or `no_results` for most songs, Soulseek simply has no good copy; the radio then uses owned songs. `ingest_timeout` means Jellyfin did not list the file within 3 minutes (check the library scan and the folder permissions).
- **Exit code 3.** The database is broken or was written by a newer build. Restore the previous image, or back up and remove `encore.db`.

## Data and reset

`encore.db` in the data folder (`/data` in Docker) holds the trigger log, the songs Encore added and recent plays. Old rows are removed automatically: plays after 14 days, triggers and added songs after 90 days. Deleting the file is safe: Encore then forgets its recent picks and its duplicate-check keys. Deleting `device-id` gives Encore a new device entry in Jellyfin.

## Upgrading and rollback

Pin a version tag. The database schema version is stored in the file itself (`PRAGMA user_version`; v0.1 uses schema 1, v0.2 schema 2). The first v0.2 start migrates a v0.1 file and first copies it to `encore.db.v1.bak`; to go back to v0.1.x, stop Encore, restore that copy over `encore.db` and run the old tag (v0.1 exits with code 3 on a schema 2 file). To roll back, run the previous tag. A database from a newer build makes the older build exit with code 3, so back up `encore.db` before an upgrade.
