# JellyEncore

Encore is a small .NET 10 service that runs alongside a Jellyfin 12.4 server. When a listed user's music queue is down to one song after the current one, Encore adds more songs to the queue, so the music keeps going. v0.1 uses only songs already in the library. v0.2 can also fetch songs the server does not have from Soulseek (through slskd), import them into the library and queue them; it is off by default. v0.3 adds song and album requests (a request page and a Jellyfin search script; albums go through Lidarr), also off by default.

## What it does

When an allow-listed user's queue is down to one song after the current one (the current song is the second-to-last), Encore adds `ENCORE_BATCH` (2) owned songs to the end of that session's queue with PlayLast. When only the last song Encore added is still to come, it adds the next batch (a top-up), so a song is always left to play and the radio keeps going for up to about 60 songs per run. If the app's next queue report does not show the songs, Encore sends them once more (only while something is playing, and never just because a request got no answer). If the second-to-last song was missed (a restart, a skip straight to the last song), the last song still starts the radio. Songs come from similar artists, similar songs and an instant mix, and never repeat within the recent window.

Works with Jellyfin Web and Finamp. Other apps are skipped (reason `unknown_client`). Nothing is added while a session has repeat on (`repeat_on`) or does not allow remote control (`no_remote_control`).

### When the radio starts

- **A queue running out.** When one song is left after the current one, the radio starts (rule `near_end`). If that moment was missed, the last song starts it (rule `last_song`).
- **A song played on its own** (v0.3.3). A song started from a stopped player, with no other songs queued, starts the radio when about 90 seconds of it are left (rule `single`), once per play. Jellyfin 12.4 does not report the app's queue until the first song change, so Encore cannot tell a song on its own from the first song of an album started from a stopped player. In that case the radio's songs are added after the album's last track, while the first track plays; the album still plays in full first, and the radio carries on after it. A song that is paused, or has more than 90 seconds left, waits (`single_waiting` in the debug log).
- **Never:** with repeat on, in an app that does not allow remote control, or in another app than Jellyfin Web or Finamp.

## Requirements

- Jellyfin 12.4. The queue rules depend on how 12.4 stores the queue.
- A Jellyfin API key made by an admin (Dashboard, API Keys). Encore needs it to see all sessions and to send PlayLast. Enter it yourself in the environment file; never put it on a command line or in the repository.
- The playing app must allow remote control, and repeat must be off.

## Layout

- `src/Encore` - the host: config, Jellyfin client, session watcher, trigger, picker, orchestrator, store, health, and the v0.2 fetch side (slskd client, Deezer and ListenBrainz discovery, scorer, importer, fetch worker), and the v0.3 request side (HTTP API and request page, sign-in guard, song and album requests, Lidarr client, notifications)
- `contrib` - scripts for Jellyfin Web (JavaScript Injector); `tests/js` - their node tests
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

## Requests (v0.3)

Off unless `ENCORE_REQUESTS=on`. When on, Encore also serves a small request page and a JSON API (`/api/v1`) so people can ask for a song or an album the library does not have. With `ENCORE_REQUESTS=off` none of this exists: no routes, no extra database use, and `/health` shows only `"requests": {"enabled": false}`.

- **Songs** come from Soulseek through the same fetch path as the radio. This needs `ENCORE_FETCH_MODE` other than `off` (`live` to really download). A requested song goes ahead of radio fetches, is tried with FLAC first, and lands in `music-radio` marked as requested. It is retried after 1 hour and 6 hours, then fails. If the library already has the song, the request is `ready` at once.
- **Albums** go to Lidarr. Encore switches the album on in Lidarr, and Soularr (which you already run) fetches it. Encore checks Lidarr every 2 minutes, asks Jellyfin to scan the new folder, and marks the request `ready` when Jellyfin lists the album. An album with no file after 7 days fails as `not_found` and is switched off again in Lidarr; one that stalls for 3 days with only some tracks fails as `incomplete`. An album that is not out yet (a release date in the future, or no date yet) can be requested too, like an unreleased movie in Radarr: Encore switches it on in Lidarr straight away, and the request waits as `upcoming` ("Upcoming: releases YYYY-MM-DD" or "Upcoming: release date not known yet"). Encore re-reads the release date from Lidarr every 6 hours; on the release day the request moves to `searching`, and only then does the 7-day `not_found` timer start. Search shows such albums with an Upcoming badge and a Request button. An upcoming request counts toward the daily cap, and its owner can cancel it.
- **Who may request**: admins, plus the users in `ENCORE_REQUEST_USERS` (names or ids, or `*` for everyone). Everyone else can search but not request. Non-admins have a daily cap (`ENCORE_REQUEST_SONGS_PER_DAY`, `ENCORE_REQUEST_ALBUMS_PER_DAY`); admins have none. There is no approval step.
- **Notifications**: when a request becomes `ready` or fails, Encore sends a message ("Ready: artist - title" or "Could not get: artist - title") to the requester's open Jellyfin apps that allow remote control. Once per state; it never changes the request.
- **Sign-in** uses Jellyfin itself. The page asks for the Jellyfin name and password (or a Quick Connect code when the server allows it) and passes them straight to Jellyfin. Encore keeps no password and sets no cookie; the token stays in the browser and is sent as a Bearer header. Wrong passwords are limited (see "Fixed limits" below).

### Request settings

| Variable | Purpose | Default |
|---|---|---|
| `ENCORE_REQUESTS` | `on` or `off` | `off` |
| `ENCORE_REQUEST_USERS` | Jellyfin names or ids who may request (comma-separated), or `*` for everyone. Admins always may. An unknown name is a startup warning | empty (admins only) |
| `ENCORE_REQUEST_SONGS_PER_DAY` | Songs per non-admin per day (0-500; 0 = songs off for non-admins) | `25` |
| `ENCORE_REQUEST_ALBUMS_PER_DAY` | Albums per non-admin per day (0-50; 0 = albums off for non-admins) | `2` |
| `ENCORE_REQUEST_ORIGINS` | Exact web origins allowed to call `/api/v1` from another site (CORS), for example `http://192.168.86.50:8096,https://media.example.com`. Empty: the request page works, no other site may call the API | empty |
| `ENCORE_PUBLIC_URL` | Encore as browsers reach it, for example `http://192.168.86.50:5095`, later the https address. Absolute http(s) URL; its path must equal `ENCORE_PATH_BASE` | - |
| `ENCORE_PATH_BASE` | Optional `/segment` when Encore is published under a path. No trailing `/` | empty |
| `ENCORE_TRUSTED_PROXIES` | Comma-separated IPs or CIDR ranges of the proxies whose `X-Forwarded-*` headers are believed (at most 64). Empty: those headers are ignored | empty |
| `ENCORE_JELLYFIN_PUBLIC_URL` | Jellyfin as browsers reach it, for the "Open in Jellyfin" links | - |
| `ENCORE_LIDARR_URL` | Lidarr base URL, for example `http://lidarr:8686`. Unset: album requests are off | - |
| `ENCORE_LIDARR_API_KEY` | Lidarr API key, required with the URL (20-64 letters and digits). Never logged or shown | - |
| `ENCORE_LIDARR_ROOT_FOLDER` | The Lidarr root folder new artists go in. Only needed when Lidarr has more than one | the only root folder |
| `ENCORE_LIDARR_PATH_MAP` | `<Lidarr's path prefix>=<Jellyfin's path prefix>`, so Encore can tell Jellyfin which folder to scan | `/media/music=/media/music` |

`ENCORE_REQUESTS=on` with `ENCORE_FETCH_MODE=off` is allowed: only album requests work, song requests answer 503 `songs_unavailable`, and Encore logs a warning at startup.

Fixed limits, not configurable: a sign-in token is re-checked with Jellyfin at most once a minute; Deezer search results are cached for 24 hours and Lidarr lookups for 1 hour; a song request waits at most 60 minutes in the fetch queue. Wrong sign-ins: 5 per IP in 15 minutes blocks that IP for 15 minutes; 10 per name in an hour locks that name for an hour (for a user with a Jellyfin lockout limit: that limit minus one in 24 hours, locked for 24 hours); 30 sign-in attempts per minute overall. 30 bad tokens in 5 minutes from one IP blocks that IP for 15 minutes.

### The request page

Open `<Encore address>/request` (for example `http://192.168.86.50:5095/request`), sign in with the Jellyfin account and search. Results come in two tabs, Songs and Albums, each with a count (Songs is shown first, or Albums when there are no songs; the page remembers your last tab). Each tab has an "In your library" part (with an Open in Jellyfin link) and the rows to request, each with a Request button or its current status. "My requests" refreshes by itself; admins also see "Everyone" and can cancel any request. `/request?q=text` opens with that search filled in.

Statuses: `queued`, `upcoming` (an album waiting for its release date), `searching`, `downloading`, `importing`, `ready`, `failed` (with the reason) and `cancelled`. A failed request has a Retry button.

An album that is `searching` first reads "Waiting for Soularr to find it on Soulseek". After 30 minutes it reads "Not found on Soulseek yet; Soularr tries again within a day", because Soularr retries an album it could not find only after its `search_retry_wait_hours` (24 hours). It still fails as not found after 7 days of searching. If Lidarr turns a requested album (or its artist) off again while Encore waits for it, which can happen when two albums of a new artist are requested within moments of each other, Encore switches it back on and logs a warning; after 3 times it marks the request failed ("Lidarr did not accept the request") and leaves Lidarr as it is.

### Installing the search script in Jellyfin Web

The script adds a "Not in library? Request" section to the Jellyfin search page. The section has the same two tabs, Songs and Albums, with counts. It sits after Jellyfin's own results and, when the Jellyfin Enhanced plugin shows its "Discover on Seerr" section, just before that one. The script only puts its section back when Jellyfin removed it (at most 3 times a second, then not again until the next search), so it never fights another script over the page order. It is optional: the request page works on its own.

1. In the JavaScript Injector plugin, import `contrib/jellyfin-request-search.import.json` (or add a script and paste the whole of `contrib/jellyfin-request-search.js`). Save.
2. Edit the two settings at the top of the script: `ENCORE_URL_HTTP` (Encore's address on your network, for example `http://192.168.86.50:5095`) and `ENCORE_URL_HTTPS` (Encore's public https address; leave it empty until Encore is published). The script uses the one that matches the page's protocol.
3. Add the address Jellyfin Web is opened from to `ENCORE_REQUEST_ORIGINS` (next section) and restart Encore.
4. Hard-refresh Jellyfin Web (Ctrl+F5) on each device.

A browser will not let a page loaded over https call an http address. So on an https Jellyfin page with no `ENCORE_URL_HTTPS`, the script shows only a link to the request page at your network address. The script sends the Jellyfin token only to Encore, and only when Jellyfin Web is talking to the same address the page came from. Its tests run with `node --test tests/js`.

### CORS and origins

The request page is served by Encore itself, so it needs no CORS. The injected script runs inside Jellyfin Web, which is a different site as far as the browser is concerned, so Encore must be told about it: put the exact origin (scheme, host and port, no path, no trailing slash) in `ENCORE_REQUEST_ORIGINS`, for example `http://192.168.86.50:8096` for Jellyfin on your network and `https://media.example.com` for its public name. Separate several origins with commas. A wrong or missing origin shows up as a CORS error in the browser console. Encore uses no cookies, so cross-site calls carry no credentials; only GET, POST and DELETE with the `Authorization` and `Content-Type` headers are allowed. The same list decides which sites may put the request page in a frame (none when empty).

### Lidarr setup and key

Album requests need Lidarr and Soularr (Soularr is the part that fetches the albums Lidarr wants).

1. In Lidarr open Settings, General, Security and copy the API key. Put it in the `.env` file as `ENCORE_LIDARR_API_KEY`; never on a command line or in the repository. A Lidarr key has full control of Lidarr; Encore uses it only to look albums up, switch albums and artists on or off, and read progress.
2. Set `ENCORE_LIDARR_URL` to Lidarr as Encore's container reaches it (container name and port, for example `http://lidarr:8686`). Both containers must share a Docker network.
3. Lidarr needs at least one root folder (Settings, Media Management). If it has several, set `ENCORE_LIDARR_ROOT_FOLDER` to the one to use. A new artist gets that folder's default quality and metadata profiles.
4. Set `ENCORE_LIDARR_PATH_MAP` if Lidarr and Jellyfin see the music at different paths (Lidarr's prefix, an equals sign, Jellyfin's prefix). Encore uses it only to ask Jellyfin to scan the right folder.
5. Make sure Soularr runs against the same Lidarr. It reads Lidarr's wanted list on its own schedule (every 5 minutes by default), so a requested album can take a while to start.

Encore adds a missing artist to Lidarr as monitored but with "monitor new items" set to none, so only the requested album is wanted and the artist's other albums stay off. If the artist was already in Lidarr but switched off, Encore switches the artist on; that can make the artist's other wanted albums start too, and Encore logs `lidarr.artist_other_wanted` (9370) when it happens. Cancelling a request switches the album off again unless another request still holds it.

### Publishing behind Nginx Proxy Manager and Cloudflare

Only publish Encore if you want requests from outside your home.

1. **What is reachable.** A request that arrives through a trusted proxy can reach only `/request*` and `/api/v1/*`, plus the bare root `/`, which answers 302 to `request` (GET and HEAD only). Everything else, including `/health`, answers 404 to it, so internal state (sessions, the slskd address, user names) never leaves your network. Read `/health` on the local address. Through the proxy, every API route except `/api/v1/config` and the sign-in routes needs a valid Jellyfin token, and only allowed users can make requests.
2. **Proxy host.** In Nginx Proxy Manager add a proxy host (for example `requests.example.com`) that forwards to Encore's container and port (8080 in the image), with an https certificate and Force SSL. Websockets are not needed.
3. **Trusted proxies.** Set `ENCORE_TRUSTED_PROXIES` to NPM's own address (for example `172.21.0.40`; give NPM a fixed IP on the network). A whole Docker network such as `172.21.0.0/16` also works, but then every container on it, and the network's gateway, can set the forwarded headers. When the name goes through Cloudflare, also list Cloudflare's published ranges (cloudflare.com/ips). Encore reads `X-Forwarded-For` from the right, and every hop it uses must itself be trusted; with only NPM listed, the "client" would be a Cloudflare server and the sign-in limits would count many people as one. Encore never reads Cloudflare's own client-IP header, which anyone reaching NPM another way could fake. With the list empty, forwarded headers are ignored. After setting it, check that `auth.rejected` log lines show a real visitor address and not a proxy's.
4. **Public address.** Set `ENCORE_PUBLIC_URL` to the https address people use, add Jellyfin's public origin to `ENCORE_REQUEST_ORIGINS`, and fill `ENCORE_URL_HTTPS` in the injected script.
5. **Under a path.** To publish under a path (for example `https://media.example.com/encore`) set `ENCORE_PATH_BASE=/encore` and `ENCORE_PUBLIC_URL=https://media.example.com/encore` (the paths must match). Encore accepts the prefix whether or not the proxy strips it, and the page builds all its links from it. With a path base set, direct requests on your network need the prefix too.
6. **Cloudflare.** The published name must resolve to Cloudflare from inside your network as well. A name that resolves to a local address for some devices makes recent browsers ask permission for "local network access" when an https page calls it.
7. **Headers.** Encore sends a content security policy, no-sniff, no-referrer and, over https, HSTS on the page and the API. A Cloudflare rate-limit rule on `/api/v1/auth/*` is a sensible extra layer on top of Encore's own sign-in limits.

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
- `warnings`: a list of strings, empty in mode `off`. Possible entries: `slskd: unreachable`, `slskd: key rejected`, `slskd: not logged in`, `music-radio is not in a Jellyfin music library`, `music-radio not scanned by Jellyfin yet (run Scan All Libraries)`, `music-radio free space below 20 GB`, `owned index not loaded` (5 minutes after start), `slskd copies left behind: N`, `deezer: failing` (a Deezer error in the last 10 minutes). With requests on: `lidarr: unreachable`, `lidarr: key rejected`, `requests: N album(s) in Lidarr but not in Jellyfin` (requested albums that Lidarr has had for over 2 hours but Jellyfin does not list yet), `requests: song fetching is off`. Request problems never change the status.
- `slskd`: `configured`, the URL (no credentials), `reachable`, `loggedIn`, `lastOkUtc`, `lastError`, `searchesInWindow` and `searchLimit` (the budget).
- `fetch`: `mode`, `available` and `unavailableReason` (`off`, `not_configured`, `slskd_unreachable`, `slskd_key_rejected`, `not_logged_in`, `radio_dir_not_in_library`, `disk_low`, `hourly_cap`, `index_not_ready`), `ownedIndex` (`loaded`, `items`, `refreshedUtc`), job counters since start (`queued`, `inFlight`, `ingesting`, `ready`, `failed`, `cancelled`, `wouldDownload`), `passedOver` and `skipped` (picks passed over or skipped at hand-out), `lastHour` against `hourlyCap`, `radioDirInLibrary` and `radioLibrary`, `freeBytes`, `sourceLeftBehind`, `lastReadyUtc` and `lastError`.
- `dryRun`: whether the service only logs.
- `socket`: `state` (`connecting`, `subscribed`, `down`), when it changed, `reconnects`, and the last message and Sessions times. An idle server sends nothing, so an old `lastSessionsUtc` alone is not a fault.
- `poll`: whether the fallback poll is running and when it last worked.
- `allowList`: the configured names, the resolved name-to-id map, names that did not resolve, and the last error.
- `triggers` and `playLast`: counters since start (fires, sent, dry runs, no picks, failures, songs sent, songs never seen in the queue).
- `store`: whether the database is working and its schema version.
- `requests`: with `ENCORE_REQUESTS=off` only `{"enabled": false}`. With requests on: `enabled`; `songs` and `albums` (`available`, or the reason: `fetch_off`, `fetch_unavailable:<reason>`, `lidarr_unreachable`, `lidarr_key_rejected`, `lidarr_not_configured`); `active` (requests now `queued`, `searching`, `downloading`, `importing`, `upcoming`); `auth` (`blockedIps`, `lockedNames`, `failedLoginsLastHour`: counts only, never names or addresses); `today` (`created`, `ready`, `failed` in the last 24 hours); `lidarr` (`configured`, `reachable`, `version`, `lastOkUtc`, `lastError`); `notify` (`sent`, `failed` since start). It never shows a key or a token.

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
| `request.created`, `request.ready`, `request.failed`, `request.cancelled` | Info | Request lifecycle (9200-9290; `request.state` logs every change, `request.limit` a daily cap hit) |
| `auth.login_limited` | Warning | A sign-in block started: per IP, per name (hashed), overall, or bad tokens (9030). `auth.rejected` (Info) logs a refused token |
| `lidarr.state`, `lidarr.request_failed`, `lidarr.auth_failed` | Info, Warning, Error | Lidarr became reachable or not, a call failed, or the key was refused (9300-9320) |
| `lidarr.artist_other_wanted` | Warning | Switching an artist on also made N other albums wanted (9370) |
| `request.search_no_albums` | Information | A search where Lidarr returned no albums (9111; counts only, no search text) |
| `notify.sent`, `notify.failed` | Info, Warning | A "Ready" or "Could not get" message went to the requester, or could not be delivered (9400, 9410) |

`trigger.skipped` reasons: `not_allowed`, `not_audio` (a video or episode), `no_remote_control`, `repeat_on`, `unknown_client`, `stale_queue`, `no_history`, `not_last` (two or more songs still to come), `duplicate`, `rate_limited`, `run_cap`, `already_queued` (songs sent at the previous song have not shown up yet; the delivery check handles them), `queue_first` (debug), `single_waiting` (debug: a song played on its own that is paused or has more than 90 s left). `trigger.fired` rules: `near_end`, `last_song`, `resend`, `single`.

## Troubleshooting

- **Nothing happens.** Read the `trigger.skipped` reasons. `repeat_on` means repeat is on for that session. `no_remote_control` means the app does not allow remote control. `unknown_client` means the app is not Jellyfin Web or Finamp. `stale_queue` and `no_history` are normal for the first song after a restart, or a song started from idle with other songs still listed from before; a song started on its own waits for its last 90 seconds (`single_waiting`).
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
- **Requests: the page or the script says CORS, or nothing happens in Jellyfin.** The address Jellyfin Web is opened from is not in `ENCORE_REQUEST_ORIGINS`. Add the exact origin (scheme, host, port) and restart. The browser console names the blocked origin.
- **Requests: the script shows only a link.** The Jellyfin page is https and `ENCORE_URL_HTTPS` in the script is empty. A browser blocks an https page from calling an http address; publish Encore over https and fill that setting.
- **Requests: sign-in says too many attempts (429 `login_limited`).** Too many wrong passwords from that address or for that name. It clears by itself (15 minutes for an address, an hour for a name, 24 hours for a user with a Jellyfin lockout limit). Behind a proxy, check that `ENCORE_TRUSTED_PROXIES` lists the proxy, or every visitor looks like the proxy and shares one limit.
- **Requests: `403 not_allowed`.** The user is not an admin and not in `ENCORE_REQUEST_USERS`.
- **Requests: `429 daily_limit`.** A non-admin used the day's songs or albums. Raise `ENCORE_REQUEST_SONGS_PER_DAY` or `ENCORE_REQUEST_ALBUMS_PER_DAY`.
- **Requests: `429 retry_limit` or `429 search_limited`.** A failed request can be retried 3 times by its owner (an admin has no limit); make a new request instead. A person may search 30 times a minute.
- **Requests: `503 songs_unavailable`.** `ENCORE_FETCH_MODE` is `off`. Songs need fetching; albums still work.
- **Requests: `503 albums_unavailable`, or `lidarr: unreachable` / `lidarr: key rejected` in `/health`.** Check `ENCORE_LIDARR_URL` and that both containers share a network, and that `ENCORE_LIDARR_API_KEY` is the key shown in Lidarr under Settings, General, Security.
- **Requests: an album stays at "In Lidarr, waiting for Jellyfin".** Lidarr has the files but Jellyfin has not listed the album (`requests: N album(s) in Lidarr but not in Jellyfin` after 2 hours). Check `ENCORE_LIDARR_PATH_MAP` (Lidarr's and Jellyfin's view of the same folder) and that the music folder is in a Jellyfin Music library; a library scan in Jellyfin will also pick it up.
- **Requests: an album never starts.** Soularr must be running against the same Lidarr; it only looks at Lidarr's wanted list every few minutes. After 7 days with no file the request fails as `not_found`.
- **Requests: `/health` answers 404 from outside.** By design: requests arriving through a trusted proxy reach only `/request*` and `/api/v1/*`. Use the local address for `/health`.
- **Exit code 3.** The database is broken or was written by a newer build. Restore the previous image, or back up and remove `encore.db`.

## Data and reset

`encore.db` in the data folder (`/data` in Docker) holds the trigger log, the songs Encore added and recent plays. Old rows are removed automatically: plays after 14 days, triggers and added songs after 90 days. Deleting the file is safe: Encore then forgets its recent picks and its duplicate-check keys. Deleting `device-id` gives Encore a new device entry in Jellyfin.

## Upgrading and rollback

Pin a version tag. The database schema version is stored in the file itself (`PRAGMA user_version`; v0.1 uses schema 1, v0.2 schema 2, v0.3 schema 3). The first v0.3 start migrates a v0.2 file the same way and first copies it to `encore.db.v2.bak`. This happens even with `ENCORE_REQUESTS=off`. To go back to v0.2.x, stop Encore, restore `encore.db.v2.bak` over `encore.db` and run the old tag (v0.2 exits with code 3 on a schema 3 file); requests and radio history since the upgrade are lost. The copy is made only when none exists, so delete an old `encore.db.v2.bak` from a test run before the first v0.3 start. The first v0.2 start migrates a v0.1 file and first copies it to `encore.db.v1.bak`; to go back to v0.1.x, stop Encore, restore that copy over `encore.db` and run the old tag (v0.1 exits with code 3 on a schema 2 file). To roll back, run the previous tag. A database from a newer build makes the older build exit with code 3, so back up `encore.db` before an upgrade.
