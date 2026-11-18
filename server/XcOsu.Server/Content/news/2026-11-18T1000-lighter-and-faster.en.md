# A lighter, faster xc-osu!

This update changes nothing you see in game: it changes how the game runs in your browser.

## Less memory

Until now, every texture, sound and track built into the game (about 130 MB) was loaded into the game's memory at startup, whether it was needed or not. They now live in a separate file kept in your browser's cache, and the game only reads what it needs, when it needs it. In our tests the game's memory use drops by about 11 to 20 %, and the main menu shows up sooner.

## More code compiled ahead of time

Part of the game's code was still interpreted, which is slower. Using a profile of what the game actually runs, only a few hundred functions are still interpreted instead of almost 4000. In our measurements each frame takes less time to compute, and the memory clean-up pauses during gameplay are about a third shorter.

## Two additions to the launcher

- **Audio thread rate**: 1000 Hz by default. On a slow machine, 500 Hz frees CPU and delays a hit sound by 1 ms at most.
- If your last session struggled to keep up, or your machine is modest, the launcher **suggests the low-end preset**. You can say no, and it won't ask again.

These figures come from our own controlled measurements: how much you gain depends on your machine. If anything feels less smooth than before, let us know.
