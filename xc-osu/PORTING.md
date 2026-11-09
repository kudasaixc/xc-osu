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
- Miroirs: catboy.best (User-Agent obligatoire) -> osu.direct -> nerinyan.
- Le JSON serveur ignore les ShouldSerialize* de SoloScoreInfo (id, user, pp...) via Api/Json.cs ServerContractResolver.
- Déploiement: `dotnet publish -c Release -o /var/www/osu/server/publish` puis `sudo systemctl restart xc-osu-server` (port 127.0.0.1:5080).
- Apache: /etc/apache2/sites-available/osu-le-ssl.conf — DocumentRoot /var/www/osu/site (le jeu publié), COOP/COEP, fichiers .br/.gz pré-compressés, proxy /api /oauth /users /notifications /signalr (upgrade=websocket).
