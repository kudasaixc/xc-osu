# Welcome to xc-osu!

xc-osu! is the full lazer client running in a browser tab. Nothing to install: open the page, wait for the first download, play.

It is not a remake and not a video stream. The game you get is the real client, compiled to WebAssembly, drawing with WebGL2 and playing sound through the browser's audio engine. Your beatmaps, skins, scores and settings are stored by the browser on your own machine and are still there the next time you come back.

## What works today

- **osu! standard**, with mods, replays and skins.
- **Beatmaps**: search and download from inside the game, or drop an `.osz` file on the page.
- **Accounts**: scores, pp, global and country rankings, friends, chat.
- **Multiplayer and spectating**, on xc-osu!'s own servers.

## What does not, yet

- taiko, catch and mania are not shipped for now.
- Background and storyboard videos do not play.
- The first launch downloads a lot. Later launches come from the browser's cache and start much faster.

## Its own servers

Everything online goes through xc-osu! itself. Accounts are created here, scores are submitted here, and pp is computed here with the same calculators the game uses. Nothing is sent to the official osu! servers, and an account made here only exists here.

## What you need

A recent Chromium-based browser (Chrome, Edge, Brave, Opera) on a computer is the best-tested setup. Firefox works and gets less testing. Hardware acceleration has to be on: without a real GPU the game runs, but far too slowly to be played.

Have fun, and tell us what breaks.
