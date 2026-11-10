# xc-osu! — port web d'osu!lazer (code original) — notes de travail

Sources: /var/www/osu/osu (lazer bb37f05418), /var/www/osu/osu-framework (tag 2026.1030.0)
Toolchain: ~/.dotnet (SDK 10.0.403, workloads wasm-tools, wasm-experimental). env: DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
Test harness: /var/www/osu/tools/shot.mjs (playwright + chromium-1248 swiftshader)

## Architecture cible
- Thread UI navigateur = DrawThread d'osu-framework (rAF), contexte WebGL2 via emscripten (DllImport -> libGL emscripten, glue C).
- Update/Audio threads = threads .NET (WasmEnableThreads, COOP/COEP requis).
- osuTK GL (ES30) remplacé par une classe GL statique DllImport (même API) -> GLRenderer d'origine inchangé.
- Shaders: libveldrid-spirv (shaderc+SPIRV-Cross) compilé en wasm + cache pré-rempli.
- Audio: backend Web Audio (remplace BASS), temps extrapolé côté C#.
- Realm: shim API-compatible (namespace Realms) en mémoire + persistance IndexedDB/OPFS.
- Storage: OPFS / IndexedDB.
- Tablette: ITabletHandler basé Pointer Events (pen) + WebHID optionnel.
- Serveur: API v2 lazer-compatible (ASP.NET Core) + spectator (SignalR), hébergé ici.
- Beatmaps / osu!direct: miroirs (catboy.best, nerinyan, osu.direct).
- Branding: "xc-osu!" partout, crédits xc softworks (kudasaixc).

## Statut
- [x] PoC WebGL2 depuis C# wasm (poc/GlPoc)
- [x] PoC MT: rendu sur JSWebWorker (reflection RunAsync) + OffscreenCanvas transféré via MAIN_THREAD_ASYNC_EM_ASM + GL.registerContext ; update thread séparé. NB: le thread UI navigateur ne peut PAS exécuter de C# en MT (.NET 10).
- [x] Audio: xcbass (API BASS/BASS_FX/BASSmix en C, décodeurs dr_*/stb_vorbis, tempo signalsmith) -> ring float partagé -> AudioWorklet. Build: native/xcbass/build.sh
- [x] Realm: realm-core d'origine compilé wasm+pthreads (native/build-realm.sh, `ninja realm-wrappers`, merge.mri). Patches IN PLACE dans realm-core: util/xcvfs.{hpp,cpp} (image mémoire cohérente par inode pour read/write/mmap, flock in-process), file.cpp/file_mapper.cpp via XCSYS(), external_commit_helper emscripten threadé, scheduler générique, DB fichier (pas in-memory).
- [x] Gardes navigateur (RuntimeInfo.IsBrowser): GC latency, PreloadBass, UnixNativeSleep, hard links, HttpClientHandler, hubs SignalR (version hash/session en query), websocket notifications (access_token en query).
- [x] Décodage d'images natif : stb_image (module StbiSharp `stbi`) compilé wasm+SIMD -> Native/stbi.a (native/stbi/build.sh). Sans lui tout passait par ImageSharp interprété (lent + GC = saccades des overlays).
- [x] MD5 managé (osu.Framework/Extensions/XcMd5.cs) : .NET navigateur n'a pas de provider MD5 ; utilisé si RuntimeInfo.IsBrowser.
- [x] Callbacks natif->managé avec structs par valeur (StringValue, NativeException, Schema de realm) : le générateur .NET passe `&argN` au lieu de `argN` -> tools/fix-pinvoke-structs.py lancé par la cible XcFixPInvokeStructArgs (AfterTargets=_GenerateManagedToNative). Symptôme sinon : assertion interp.c:2393 (has_resume_state) ou blocage dans Realm.GetInstance.
- [x] xcvfs::posix_fallocate : REALM_HAVE_POSIX_FALLOCATE=1 sous emscripten et File::prealloc appelait ::posix_fallocate en direct -> l'image cohérente restait à 0 octet -> boucle infinie dans DB::open (fichier .lock "vide"). Banc de test rapide : native/realm-test/run.sh (realm-core sous node, ~10 s).
- [x] Logs : Logger -> console navigateur (Important+ ; tout avec `?log` dans l'URL, + trace realm-core).
- [x] Program.cs ne dispose plus l'hôte (CanExit=false masquait la vraie exception sous "Exit must be called before the GameHost is disposed").
- [x] Branding: GAME_NAME, intro, footer réglages, SupporterDisplay, logo menu (osu.Web/Resources/Textures/Menu/logo.png via CreateOverrideResourceStores), chaînes anglaises + regex sur traductions (ResourceManagerLocalisationStore.Branding).

## Serveur xc-osu! (/var/www/osu/server/XcOsu.Server)
- ASP.NET Core net10 référençant osu.Game + osu.Game.Rulesets.Osu (modèles Newtonsoft/MessagePack d'origine, pp via les calculateurs d'osu!).
- SQLite /var/lib/xc-osu/xc-osu.db, cache .osu dans /var/lib/xc-osu/osu-files, replays .osr (enregistrés via spectator) dans /var/lib/xc-osu/replays.
- Endpoints: /oauth/token, POST /users, /api/v2/* (me, users, beatmaps, beatmapsets/search via miroirs, download proxy, solo scores + pp, leaderboards, rankings, chat, friends/blocks, favourites, rooms/playlists, news...), /notifications (ws chat), /signalr/{spectator,multiplayer,metadata}.
- Online complet (2026-11-10) : medals (Services/Medals.cs) + notifications live, commentaires/votes, tags de beatmap (Services/Tags.cs), scores épinglés, recent_activity, rank history (snapshot quotidien), daily challenge (Services/DailyChallenge.cs : salle playlists quotidienne, streaks, top 10%/50%), quick play + ranked play (Services/Matchmaking.cs : file, invitations, stages, cartes, Elo dans matchmaking_ratings), salons de chat par room, teams (/api/xc/teams*), avatars/covers/flags servis par le serveur (/api/xc/avatar|cover|team-flag), page de compte /api/xc/settings (Assets/settings.html).
- Pièges : les propriétés setter-only d'APIUser (rank_history, playstyle) ne sont jamais sérialisées -> injectées à la main (Endpoints.fullJson). SoloScoreInfo.BeatmapSet ne doit jamais être assigné quand Beatmap est null (exception).
- Miroirs: catboy.best (User-Agent obligatoire) -> osu.direct -> nerinyan pour sets/maps/téléchargements.
- Recherche du listing (Services/BeatmapSearch.cs, 2026-11-10) : osu.direct (Meilisearch : `sort=champ:asc|desc`, amount<=100, offset, UN seul `status` et `mode` ; genre/langue/extras ignorés) ; catboy en secours (ignore le tri, résultats lointains). Un flux par statut (Has leaderboard = 1,2,3,4) fusionnés sur la clé de tri, curseur `cursor[offsets]=a.b.c.d`. Syntaxe osu-web (ar>9 stars>=6 length<3m keys=7 status= creator= "phrase" ranked=2020…) parsée et filtrée localement ; tous les mots doivent matcher (tolérance fautes à la Meili) et un flux s'arrête quand une page du miroir a <5 vraies correspondances (Meili retire des mots sinon). Limite osu.direct 120/min, burst 25/5s : max 6 appels/recherche, pause sur 429/remaining=0, cache mémoire 5 min. Le listing n'envoie plus related_tags par set (pages de ~1 Mo -> ~200 Ko).
- Test E2E isolé : instance sur 127.0.0.1:5081 avec DataDirectory dans un dossier temporaire, scripts HTTP python + client SignalR C# (MessagePack + SignalRUnionWorkaroundResolver) qui joue quick/ranked play.
- Le JSON serveur ignore les ShouldSerialize* de SoloScoreInfo (id, user, pp...) via Api/Json.cs ServerContractResolver.
- Déploiement: `dotnet publish -c Release -o /var/www/osu/server/publish` puis `sudo systemctl restart xc-osu-server` (port 127.0.0.1:5080).
- Apache: /etc/apache2/sites-available/osu-le-ssl.conf — DocumentRoot /var/www/osu/site (le jeu publié), COOP/COEP, fichiers .br/.gz pré-compressés, proxy /api /oauth /users /notifications /signalr (upgrade=websocket).

## Launcher (wwwroot/main.js + js/xc-*.js, 2026-11-10)
- main.js : `dotnet.download()` dès l'ouverture (assets seulement), launcher pendant ce temps, puis `withConfig/withEnvironmentVariable` + `create()` au clic sur Lancer (vérifié : aucun asset retéléchargé, pthreadPoolInitialSize pris après download).
- Options -> mécanismes réels : XC_FRAMESYNC / XC_MAX_DRAW_HZ / XC_MAX_UPDATE_HZ / XC_INACTIVE_HZ (WebGameHost.SetupConfig + GameHost.AdjustFrameLimits), DOTNET_PROCESSOR_COUNT, XC_THREADPOOL_MAX (Program.applyLaunchOptions), MONO_GC_PARAMS soft-heap-limit / max-heap-size / nursery-size, pthreadPoolInitialSize, globalThis.xcGlAttributes (xcweb.c, envoyé au worker avec l'OffscreenCanvas), globalThis.xcRenderScale (xc-ui.js), latencyHint AudioContext.
- Logs : XC_LOG=boot|verbose|quiet (boot = verbeux jusqu'à « entered Intro/MainMenu », ligne toujours émise, + « [launch] boot complete »). XC_REALM_TRACE séparé. ?log = ancien comportement (tout).
- Écran de boot (xc-boot.js) : étapes pilotées par les vrais logs (regex dans STAGES) ; console en jeu Ctrl+Alt+L, graphiques perf Ctrl+Alt+P (xc-perf.js, parse les lignes [perf] de Program.cs, XC_PERF_MS).
- Contenu : wwwroot/launcher/news.json (posts, progress, issues — à éditer à la main) ; launcher/build.json généré par tools/portstats.py (appelé par devpub/aotpub : version lazer via git describe, % de code upstream gardé via git diff --numstat, third_party exclu).
- ?launcher force le launcher (si « passer le launcher » est coché), ?launch / ?play le saute.
- stamp.py est générique : stampe aussi les imports entre js/*.js (xc-boot -> xc-log) ; même ?v= partout = même instance de module.

## Frame pacing (2026-11-10) — ne pas régresser
Symptôme : saccades / micro-retours en arrière dans les overlays en ligne (listing, profil) surtout au scroll, absents de l'ancien build /aot du 9 nov. Les correctifs de ce build n'étaient plus dans les sources ; réintégrés :
- Draw worker : pump rAF direct (`xcRafLoop` dans xcweb.c -> `WebNative.RequestAnimationFrameLoop`, callback synchrone dans l'animation frame). L'ancien `await xcRaf()` (promise -> Task -> sync context par image) reste derrière XC_DRAW_LEGACY=1.
- `GameHost.DrawReadTimeout` (virtuel) : 0 sur le web. Le TripleBuffer d'origine fait un spin jusqu'à 100 ms en attendant une frame update, ce qui fait rater des rafraîchissements au worker. XC_DRAW_WAIT=1 = comportement d'origine.
- Canvas `desynchronized: false` par défaut (option launcher « faible latence » désactivée par défaut, images dans le désordre sinon).
- Nursery GC 32 Mo par défaut (4 Mo par défaut de Mono = plusieurs GC mineures stop-the-world par seconde). Les options launcher enregistrées avant sont migrées une fois (champ `rev`, OPTIONS_REV dans xc-launcher.js).
- Sonde : la ligne `[perf]` contient `raf=N avg= max= late= presented= empty= work=` (WebDrawWorker.TakeStats).

## Build AOT (tools/aotpub.sh)
- Jobs AOT parallèles bornés par la RAM via DOTNET_PROCESSOR_COUNT (3 sous 15 Go, tous les coeurs au-dessus ; XC_AOT_JOBS=n pour forcer).
- osuTK est trimmé (TrimmableAssembly + XcTrim.osuTK.xml qui garde osuTK, osuTK.Input, osuTK.Graphics entiers) : 6,3 Mo -> 1,1 Mo d'IL, objet AOT 28 Mo -> 3,7 Mo.
- Pas de DWARF (WasmNativeDebugSymbols=false ; XC_SYMBOLS=1 pour le remettre). La symbol map reste générée.
- tools/prune-framework.py supprime les anciens fichiers fingerprintés de out/aot (ils s'accumulaient : 250 Mo).

## Frame pacing du draw worker (2026-11-10)
Le build /aot du 9 nov. (22:42) contenait des correctifs de pacing dont les sources C# n'étaient plus sur le VPS ; le build principal (launcher) les avait donc perdus -> saccades / « mini rollbacks » dans les overlays en ligne (listing, profil) et au scroll. Réintégrés :
- **Pump rAF direct** : `xcRafLoop(cb)` (xcweb.c) + `WebNative.RequestAnimationFrameLoop(Action<double>)` ; la frame s'exécute dans le callback rAF (plus de saut Promise -> Task -> SynchronizationContext par frame). `XC_DRAW_LEGACY=1` = ancien chemin attendu.
- **Draw sans attente** : `GameHost.DrawReadTimeout` (virtuel) vaut 0 sur le web. Le `TripleBuffer.GetForRead(100)` d'origine est une boucle active de 100 ms : dès que l'update thread accroche, le worker ratait plusieurs rafraîchissements. `XC_DRAW_WAIT=1` = comportement d'origine.
- **Canvas non désynchronisé par défaut** (`desynchronized:false`) ; l'option launcher « canvas faible latence » est désactivée par défaut (provoque des images dans le désordre).
- **Nursery GC 32 Mo par défaut** (launcher) : la nursery Mono de 4 Mo se remplit plusieurs fois par seconde, chaque collection mineure arrête tous les threads (draw worker compris).
- Options launcher sauvegardées avant ce correctif : `rev` 2 dans `xc.launcher.v1` remet une fois `nursery` et `lowLatency` aux nouveaux défauts.
- Stats de pacing dans la ligne `[perf]` : `raf=N avg= max= late= presented= empty= work=` (`WebDrawWorker.TakeStats`).
- Non retrouvé : le plafond d'update (`XC_UPDATE_UNCAPPED` dans l'ancien binaire) — valeur inconnue, non réimplémenté ; `XC_MAX_UPDATE_HZ` (launcher) reste disponible.

## Build AOT (tools/aotpub.sh)
- Étapes AOT en parallèle, bornées par la RAM via `DOTNET_PROCESSOR_COUNT` (3 jobs sous 15 Go ; `XC_AOT_JOBS=n` pour forcer).
- osuTK trimmé (`TrimmableAssembly` + `XcTrim.osuTK.xml` qui garde osuTK, osuTK.Input, osuTK.Graphics en entier) : 6,3 Mo -> 1,1 Mo, les bindings GL/AL desktop n'étaient compilés pour rien.
- `WasmNativeDebugSymbols=false` (plus de `-g`) ; `XC_SYMBOLS=1` pour le remettre.
- `tools/prune-framework.py` supprime les anciens fichiers fingerprintés de out/aot (252 Mo au premier passage).
- `tools/bench.mjs` : pilote Chromium avec des options launcher injectées (`XC_OPTS`), pour mesurer `[perf]` sur un build servi en local avec un serveur isolé.

### Suite (2026-11-10) — crash de la pompe rAF directe, cache du gros asset
- La pompe rAF directe plantait juste après l'intro (`JSWebWorker was disposed while running` → FailFast) : la `TaskCompletionSource` qui garde `loop()` en attente n'était référencée par rien, le premier GC finalisait le JSWebWorker. Corrigé : `keepAlive` / `workerTask` statiques dans `WebDrawWorker`.
- `main.js` force `XC_DRAW_LEGACY=1` par défaut (boucle rAF attendue, état validé en jeu). `XC_DRAW_LEGACY=0` active la pompe directe (non validée sur un vrai GPU).
- `osu.Game.Resources` (135 Mo) dépasse la taille max d'une entrée du cache HTTP de Chrome : `main.js` le garde dans Cache Storage (`xc-big-assets-v1`) via `withResourceLoader`.
- Serveur : `profile_json.badges` (`[{image, description, awarded_at, url}]`, images `Assets/badge-<image>`) et `profile_json.title`, attribués en base uniquement.

### Performance telemetry (2026-11-10)

Launcher diagnostics includes an enabled-by-default, persisted telemetry switch (`XC_TM`).
The C# collector in `osu.Web/Telemetry.cs` measures draw/update/audio/input frames,
10-second histograms, GC pauses/allocation, texture uploads, screen/overlay context,
and bounded individual stutter samples every 2 seconds (overflow counts are reported).
`js/xc-telemetry.js` adds GPU/browser/hardware, boot duration, long tasks, audio state,
visibility and error/warning messages. Each distinct message is sent once per session, truncated to
300 characters and scrubbed (URL queries and numeric path parts, e-mails, tokens, long identifiers);
repeats are only counted (`log-repeats`). Other console/chat/account content is not uploaded.
Boot checkpoints (`download-done`, `runtime-created`, `attached`, `gate-shown`, `start-click`, `gate-open`,
plus `download-failed` / `runtime-failed`) show where a session that never reached the menu stopped.
GC pause: Mono does not implement `GC.GetTotalPauseDuration()` (always 0), so the pause is estimated from
stalls of the 1 kHz input thread during frames in which a collection happened (`gc.est = 1` in windows).

Batches POST to `/api/xc/tm` every 2 seconds. Each page has a random session ID and a
48 KB localStorage outbox. Server sequence acknowledgements remove delivered items;
pagehide/hidden sendBeacon leaves the outbox intact for retry. Pending sessions retry
on the next opted-in launch (at most 8 retained sessions, 7 days). Opt-out clears pending
outboxes. With unavailable storage, live uploads still work but reload recovery cannot.
Power loss / OS kill / unavailable network cannot guarantee immediate final delivery.
The last in-memory engine samples (up to 2 seconds; aggregate window up to 10 seconds)
can be lost. No network delivery occurs after a user has opted out.

Server writes `/var/lib/xc-osu/telemetry.db`, separately from game/account data, with
45-day retention and ingestion/storage limits. No request address is read by telemetry;
Apache excludes `/api/xc/tm` from its access log. Other site access logging is unchanged.
Read reports with `python3 tools/tmreport.py`; test transport with
`node tools/telemetry-test.mjs`. Tests use a mocked endpoint in an actual browser.

Stutter flight recorder: a fixed 4096-entry ring records every draw frametime,
work duration, GC counters/pause, texture upload count/queue and latest update work.
A stutter freezes 2 seconds of history plus 2 seconds after the trigger. Nearby
triggers share the same capture; at most one new capture per 10 seconds. The capture
reports suppressed triggers and overflow, and splits into 100-frame events with
part counts so incomplete delivery is detectable. Browser events from the same
window include screen/overlay changes, resource timing (type/bytes/duration, no URL),
long tasks and explicit audio/UI worker bridge messages (operation only, no payload).
This is not instrumentation of all internal runtime worker messages or all networking.
Capture timestamps are epoch milliseconds; frame timestamps are offsets from trigger.
A 256 KB persistent outbox now sends 48 KB packets, allowing multi-part captures.
`tmreport.py` reports observed GC/texture correlations by GPU near the trigger,
with sample sizes and incomplete captures. These are sampled correlations, not causes.

### iPhone/iPad startup profile (2026-11-10)

The iOS preset selects 4 initial workers with no spare pool, 2 reported CPUs,
60 Hz draw / 240 Hz update, 512 MiB soft GC target, 4 MiB nursery, playback audio
latency and 50% render scale capped at device-pixel ratio 1. The `iosCompat` switch
also prevents automatic fullscreen and pointer lock. The profile is applied once
on iPhone/iPad (including iPad desktop UA), preserving telemetry opt-out and language;
subsequent choices are retained via `iosDefaultsVersion`. It remains selectable on
all devices. Fullscreen API absence, synchronous exceptions and rejected promises
cannot abort the start gate anymore. `tools/ios-preset-test.mjs` covers these cases.
This mitigates startup pressure and fixes a concrete gate error; physical-device
memory kills still require iPhone validation. No native/AOT rebuild is needed.

### Local telemetry viewer

Launcher → Options → Diagnostics & performance graphs → View local telemetry. The browser retains up to three sessions of 256 KB each independently of server acknowledgements. The viewer shows stutter frame intervals, browser context events, raw GC/texture/frame data, and session JSON; export and delete operate locally. Disabling diagnostics stops new collection on the next launch but preserves existing local history until deleted. Older records are evicted at the limit; partial captures are labelled. No previous server-only sessions are reconstructed. Run `node tools/telemetry-local-test.mjs` to verify archive persistence, opt-out, viewing, export, deletion and launcher integration.

### Stutter pacing and performance history (pacing1)

The web host defaults to a maximum of 240 update iterations/second, preserving explicit launcher caps (even higher ones) and any lower game limit. The old unlimited path could rebuild the entire drawable tree close to 1000 times/second for a 60 Hz canvas. This change reduces redundant work; it does not establish that every synchronous loading stall is fixed.

Flight recordings now trigger only on missed display deadlines or two repeated frames, not isolated slow CPU callbacks that still present on time. Stalls of at least 100 ms bypass the sampling cooldown when no recording is active. Background resumption (> 1 s) is excluded. The update-work column now holds the maximum since the last draw, instead of the last update callback (which could conceal a spike). Slow-update records also contain scheduler, subtree, masking and draw-node preparation durations.

`python3 tools/performance-history.py` reads telemetry in a consistent read-only transaction and maintains aggregate snapshots in `out/performance/`, with an optional `--public site/launcher/performance.json`. The launcher Status tab presents this history. `deploy/xc-osu-performance.{service,timer}` refreshes it every 15 minutes; install these in systemd on the production host. Deployment preserves the generated public history. No individual session IDs are exported. Baseline snapshots are committed under `docs/performance/`.

Mean frame interval is weighted by the histogram sample count. P99 is reported as a histogram bucket range, never as an exact percentile. Grouping includes build, GPU, browser family, core count, screen size, DPR, refresh rate and relevant launcher options. Visible windows include startup/transitions; periods exceeding one second are excluded by the engine. The maximum retained aggregate snapshot for a group is preserved if raw telemetry expires. Headless software-rendered checks are validation, not comparable to hardware user sessions.

Checks: `dotnet run --project tools/tests/stutter-policy/StutterPolicy.csproj`, `python3 tools/performance-history-test.py`, and the browser telemetry tests. The baseline and future observations may differ in workload; no automatic improvement claim is made.

Telemetry build IDs include a fingerprint of the published engine assets as well as the page revision, so an engine-only rebuild cannot silently merge into the previous version’s history. `python3 tools/stamp-test.py` verifies this and repeated stamping stability.

### Input, pointer lock and fullscreen in the browser (2026-11-10)

All in `wwwroot/js/xc-ui.js` and `Platform/Web/WebWindow.cs`.

- **Stuck keys.** A browser never delivers the key-up of a key still held when the page loses focus (Alt+Tab, tab
  switch, leaving fullscreen). The engine kept it pressed: a stuck Alt turns every scroll and arrow key into a volume
  change and breaks every shortcut. `xc-ui.js` tracks held keys / mouse buttons, releases them all on `blur`, hidden,
  `pagehide` and fullscreen changes, and drops stale modifiers using the modifier flags carried by every key, pointer
  and wheel event.
- **Pointer lock only while confined.** A page cannot warp the OS cursor, so the stock "leave relative mode at the
  window edge" made the cursor jump back to where the lock started. `WebWindow.RelativeMouseMode` is now only granted
  while `CursorState.Confined` is set (fullscreen, or `ConfineMouseMode.Always` = gameplay with osu!'s default
  setting); the cursor is then clamped to the canvas by `UserInputManager`. In windowed menus the absolute cursor is
  used and leaves the canvas freely. `WebWindow` binds `FrameworkSetting.ConfineMouseMode` itself (the SDL window did).
- **Fullscreen state is the browser's.** `fullscreenchange` writes the `FULLSCREEN` slot; `WebWindow.syncFullscreen`
  mirrors it into `FrameworkSetting.WindowMode` on the update thread without sending a request back. The setting is
  reset to windowed at startup (a page always starts windowed). A refused `requestFullscreen` (no user activation)
  stays pending and is retried on the next key press / click.
- **Escape.** While fullscreen, `navigator.keyboard.lock(['Escape'])` (Chromium) delivers Escape to the game instead of
  leaving fullscreen; holding Escape still exits. F11 and Alt+Enter toggle fullscreen (`FrameworkAction.ToggleFullscreen`
  -> `CycleMode`). Firefox / Safari have no Keyboard Lock: Escape still leaves fullscreen there.
- **Wheel.** Notches are detected with `wheelDeltaY % 120` (the pixel delta is 100 only at 100 % scaling), everything
  else is a precise (touchpad) scroll; Firefox line deltas are 3 lines per notch.

### Audio latency: direct worklet voices and the latency probe (2026-11-10)

Measured on Chrome / Windows (RTX 3060 Ti PC, 48 kHz, `?audiolat`): a sample used to be heard 85-112 ms after the key
press (up to ~165 ms after a few underruns), of which only ~50 ms is the browser's own output. Now ~63 ms.

| stage | before | now |
|---|---|---|
| press -> play request (input thread, update frame, audio thread) | 6-7 ms | 6-7 ms |
| request -> first mixed block | 14-26 ms | 3-10 ms |
| mixer ring | 10-16 ms, up to 85 ms after underruns | 0 (bypassed) |
| browser buffer + device (`baseLatency` + `outputLatency`) | 10 + 40 ms | 10 + 40 ms |

- **Direct voices** (`native/xcbass/xcbass.c` "direct voices", `js/xc-audio-worklet.js`): a one-shot sample channel with
  no effect anywhere up to the device is described in a 32-slot shared table (PCM address, length, pitch step, gains)
  and the AudioWorklet mixes its PCM straight into the block it renders, with linear interpolation. The channel still
  runs through the mixer for bookkeeping (position, end sync, levels) but its ring output is discarded. Looping
  samples, channels behind an effect and paused mixers keep the ring path. Volume / pan / frequency changes update the
  slot; seeking, pausing, removing or freeing the channel stops the voice; `BASS_SampleFree` waits until no voice
  reads the PCM. The page re-sends the wasm memory to the worklet when it grows (a sample beyond what the worklet sees
  falls back to the ring).
- **Ring depth** no longer grows forever: +256 frames per underrun (max 2048), -128 every 3 clean seconds down to the
  base (1024 frames = 21 ms; raised when a device underruns right after a reduction). It only affects dropouts, the
  music position is reported as heard.
- **Resampler read-ahead**: `kid_render` pulled up to 1024 source frames at once, so the 44.1 kHz osu! mixers were
  rendered ~21 ms ahead of the 48 kHz device. It now reads what the block consumes.
- **Probe**: the page stamps every key / button press (`xcAudioMarkInput`), xcbass timestamps each sample start and
  the worklet (or the mixer thread) the block that first contains it. `?audiolat` (or the perf overlay) prints one
  line per path every 5 s; `xcAudioDebug().lat` holds the last figures and telemetry windows carry them (`js.audio`).
  `output` is what the browser reports; a page cannot lower it (every `latencyHint` and sample rate gives 10 + 40 ms
  on that PC). True speaker latency needs a loopback measurement.
- `native/xcbass/build.sh` now works from any directory (it resolved its paths from `$0` after a `cd`).
