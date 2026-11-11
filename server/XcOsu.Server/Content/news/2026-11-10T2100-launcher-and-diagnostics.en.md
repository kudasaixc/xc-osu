# A launcher, an iPhone preset, and what diagnostics send

Opening xc-osu! no longer drops you straight into a loading bar. There is now a launcher: a page that appears at once, while the engine downloads behind it.

## The launcher

The launcher shows the news, the state of the port and the known problems. It is also where the settings that must be chosen before the game starts live: frame sync, update rate, render resolution, memory limits, graphics card preference, audio latency. They apply at the next launch. If you would rather not see it, tick the box to skip it; adding `?launcher` to the address brings it back.

## iPhone and iPad

Phones and tablets have far less memory to give to a tab than a computer does. The iPhone / iPad preset is applied automatically on those devices: half render resolution, fewer background workers, a smaller memory target, no automatic fullscreen. It makes starting the game more likely to succeed. It does not make an iPhone a comfortable way to play, and some devices still close the tab for using too much memory.

## Diagnostics

The game sends performance measurements, and you can turn that off in the launcher's options. Since it is on by default, here is exactly what it covers.

**What is sent:** frame timings and how many frames were late, pauses caused by memory collection, the name of your graphics card and browser, screen size, how long the game took to start, which screen of the game you were on, and error messages from the engine. When the game crashes or the tab dies, the last lines of the engine's log.

**What is not:** your chat, your account, your beatmaps, what you type. Messages are cut to a short length and scrubbed of addresses, tokens and e-mails before leaving your browser. The server does not record your IP address with these measurements, and they are deleted after 45 days.

**What it is for:** finding out which graphics cards and browsers stutter, and whether a new build made things better or worse. The Status tab of the launcher shows the result, grouped by build and by hardware.

Two shortcuts go with it: `Ctrl+Alt+P` shows the performance graph in game, `Ctrl+Alt+L` shows the engine's console.
