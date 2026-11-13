# xc-osu!

**osu!lazer's original C# code (osu!standard, menus, skins, replays, multiplayer) running natively in the browser on WebAssembly.**

Web port by **xc softworks** (kudasaixc) · https://osu.xcsoftworks.com

> xc-osu! is a fan-made, non-commercial web port. osu! and osu!lazer are by ppy Pty Ltd (MIT licence);
> this project is not affiliated with or endorsed by ppy.

## What's in here

| Path | What |
|---|---|
| `osu/` | osu!lazer source (fork): browser guards, xc-osu! branding, `osu.Web` host project |
| `osu-framework/` | osu-framework (fork): `Platform/Web` holds the WebGL2 renderer on an OffscreenCanvas worker, web window/input, WebHID + Pointer Events tablet handler (area, rotation, pressure), managed MD5, same-origin proxying |
| `server/XcOsu.Server` | ASP.NET Core (net10) server: osu! API v2, OAuth, scores + pp (osu!'s own calculators), rankings, chat, rooms, SignalR spectator / multiplayer / metadata hubs, beatmap mirrors (catboy.best → osu.direct → nerinyan), asset proxy |
| `native/xcbass` | BASS / BASS_FX / BASSmix API reimplemented in C → AudioWorklet |
| `native/realm-dotnet` | Realm .NET wrappers + realm-core, patched for wasm (`util/xcvfs.*`: coherent in-memory file images, mmap, flock) |
| `native/veldrid-spirv` | shaderc + SPIRV-Cross compiled to wasm for runtime shader translation |
| `native/realm-test` | fast realm-core sanity test under node |
| `web/` | the website served under `/web/` (profiles, account settings, rankings, beatmaps, teams, news, wiki, changelog): plain ES modules, no build step; deploy with `tools/deploy-web.sh`, test with `tools/web-test.mjs` |
| `server/XcOsu.Server/Content` | written content: news posts, wiki pages (en/fr) and `changelog.json` |
| `tools/` | dev and AOT publish (`devpub.sh`, `aotpub.sh`), deploy scripts, headless boot test (`shot.mjs`), telemetry tests, pinvoke struct-ABI fixer |
| `deploy/` | Apache vhost (COOP/COEP, precompressed assets, reverse proxy) and systemd unit |
| `xc-osu/PORTING.md` | porting notes: every non-obvious fix and why |

## Building

Requirements: .NET SDK 10 with `wasm-tools` and `wasm-experimental` workloads, emscripten 3.1.56 (from the workload pack).

```sh
source native/emenv.sh
native/xcbass/build.sh          # audio
native/build-realm.sh           # realm (then: ninja realm-wrappers, emar -M < native/realm-merge.mri)
tools/devpub.sh                 # publishes the client to out/dev/wwwroot
tools/aotpub.sh                 # release build (mixed AOT) to out/aot/wwwroot; needs ~8 GB of free RAM, 15 GB+ to compile in parallel
cd server/XcOsu.Server && dotnet publish -c Release -o ../publish
```

The page must be served with `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp` (multithreaded wasm). Add `?log` to the URL for verbose logs in the devtools console.

## Database

The server keeps all state in PostgreSQL 16+. `sudo deploy/setup-isolation.sh` creates the `xc-osu` role and the
`xc_osu` database it owns; the service connects over the local Unix socket with peer authentication
(`Database:ConnectionString` defaults to `Host=/var/run/postgresql;Database=xc_osu`). A non-default server can be
pointed with `Database__ConnectionString`. The former SQLite files are loaded once with
`dotnet xc-osu-server.dll import-sqlite /var/lib/xc-osu` while the service is stopped.

## Licence

MIT, see `LICENSE`. Upstream components keep their own licences (`osu/LICENCE`, `osu-framework/LICENCE`, `native/realm-dotnet/LICENSE`, …).

The game resources pulled in through `ppy.osu.Game.Resources` ([osu-resources](https://github.com/ppy/osu-resources), © ppy Pty Ltd) are
under CC BY-NC 4.0, so any deployment must stay non-commercial. "osu!" and "ppy" are trademarks of ppy Pty Ltd and are not
covered by any of these licences.
