# Recent telemetry review: 11 November 2026

Read-only review of `/var/lib/xc-osu/telemetry.db`; primary interval 14:00–16:50:13 UTC (20 sessions with events), plus today's earlier mod-menu captures. Production was not deployed or restarted. Multiple engine fingerprints occur in this interval; these observations are not a before/after benchmark.

## Concrete errors

- 16:41:09, MainMenu/chat: `RegexMatchTimeoutException` in `MessageFormatter.handleAdvanced`. Already addressed by commit `0a901cba` with a forced-timeout regression test.
- 14:36:49, SpectatorResultsScreen: `RefreshFriends` invoked a disposed SignalR `HubConnection`. Capture the connection once, tolerate its disposal/disconnection, and unbind the friends-list subscription during metadata disposal. A disposed real `HubConnection` is used in the regression test. Disconnected `GetChangesSince` now returns an actually cancelled task rather than attempting `Task.FromCanceled` with a non-cancelled token.
- 15:28:35, PlayerLoader: only `System.GC.GuardedFinalize` and the runtime finalizer thread are recorded. This does not establish an offending class, native call or exception. The next launch reports an unclean exit. No speculative audio/runtime change is claimed as a fix for that crash.
- Other unclean-exit markers include hidden tabs. They do not by themselves establish a game crash.

## Stutters

- At 12:17:29, mod-menu capture: update 112.52 ms, GC pause 99.31 ms, draw-node phase 101.34 ms; input/audio gaps also exceed 100 ms. Most of this captured delay coincides with GC, not steady rendering work. Another opening on software rendering at 01:25:33 records tree updates of 77–99 ms.
- The mod columns attached all preloaded panel trees at once. In browser builds, attach one panel per column per update instead. Keep pending columns present, preserve ordering/search and dispose unattached trees when superseded or destroyed.
- Beatmap-listing captures on older afternoon builds include tree updates of 272–375 ms and a 96.49 ms GC pause. Browser listings now attach one preloaded card per update, covering initial results, pagination and card-size changes. Pending cards count toward replacement ordering and prevent requesting another page too early.
- Large `draw:starve` values can represent accumulated repeated frames and include boot/hidden-tab activity. They must not all be interpreted as a single active rendering task.

These changes bound the number of newly attached trees, not the CPU time of an individual tree or a runtime GC. Actual frame-time improvement and visual behaviour on Windows/Edge/Opera require the owner's next browser-game build. No FPS or latency improvement is asserted without a new measurement.

## Diagnostic correction

The page log collector returned early for every line in a suppressed warning burst, including a new fatal error. Fatal lines now escape suppression, so a runtime assertion immediately following repeated warnings is retained with its following stack. The browser regression test reproduces this exact sequence. Resource-context end times use start plus non-negative duration to avoid negative cached/restricted timing entries.

## Validation

- C# regression runner: staged attachment ordering, one attachment per call, superseded-tree disposal, connection disposal/disconnection, chat timeout, profile parsing and password-recovery invariants.
- Playwright telemetry crash suite: fatal-after-suppression case, grouped stacks, redaction, repeat cap, crash persistence and opt-out.
- News, launcher and changelog updated in English and French for version `2026.1111.3`.
