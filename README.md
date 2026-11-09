# xc-osu!

**osu!lazer's original C# code — osu!standard, menus, skins, replays, multiplayer — running natively in the browser on WebAssembly.**

Web port by **xc softworks** (kudasaixc) · https://osu.xcsoftworks.com

> xc-osu! is a fan-made, non-commercial web port. osu! and osu!lazer are by ppy Pty Ltd (MIT licence);
> this project is not affiliated with or endorsed by ppy.

## What's in here

| Path | What |
|---|---|
| `osu/` | osu!lazer source (fork) — browser guards, xc-osu! branding, `osu.Web` host project |
| `osu-framework/` | osu-framework (fork) — `Platform/Web`: WebGL2 renderer on an OffscreenCanvas worker, web window/input, WebHID + Pointer Events tablet handler (area, rotation, pressure), managed MD5, same-origin proxying |
| `server/XcOsu.Server` | ASP.NET Core (net10) server: osu! API v2, OAuth, scores + pp (osu!'s own calculators), rankings, chat, rooms, SignalR spectator / multiplayer / metadata hubs, beatmap mirrors (catboy.best → osu.direct → nerinyan), asset proxy |
| `native/xcbass` | BASS / BASS_FX / BASSmix API reimplemented in C → AudioWorklet |
| `native/realm-dotnet` | Realm .NET wrappers + realm-core, patched for wasm (`util/xcvfs.*`: coherent in-memory file images, mmap, flock) |
| `native/veldrid-spirv` | shaderc + SPIRV-Cross compiled to wasm for runtime shader translation |
| `native/realm-test` | fast realm-core sanity test under node |
| `tools/` | dev publish, headless boot test (`shot.mjs`), pinvoke struct-ABI fixer |
| `deploy/` | Apache vhost (COOP/COEP, precompressed assets, reverse proxy) and systemd unit |
| `xc-osu/PORTING.md` | porting notes — every non-obvious fix and why |

## Building

Requirements: .NET SDK 10 with `wasm-tools` and `wasm-experimental` workloads, emscripten 3.1.56 (from the workload pack).

```sh
source native/emenv.sh
native/xcbass/build.sh          # audio
native/build-realm.sh           # realm (then: ninja realm-wrappers, emar -M < native/realm-merge.mri)
tools/devpub.sh                 # publishes the client to out/dev/wwwroot
cd server/XcOsu.Server && dotnet publish -c Release -o ../publish
```

The page must be served with `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp` (multithreaded wasm). Add `?log` to the URL for verbose logs in the devtools console.

## Licence

MIT — see `LICENSE`. Upstream components keep their own licences (`osu/LICENCE`, `osu-framework/LICENCE`, `native/realm-dotnet/LICENSE`, …).
