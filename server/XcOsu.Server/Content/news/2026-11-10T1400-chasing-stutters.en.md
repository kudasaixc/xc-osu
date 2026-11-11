# Chasing stutters

The first public build ran, but it did not run evenly. Menus hitched, online panels jumped back a few pixels while scrolling, and a held frame every second or so made fast maps unpleasant. Here is what was causing it and what changed.

## The garbage collector was stopping the game several times a second

The game allocates small objects constantly. The runtime collects them in a "nursery" which was 4 MB by default, small enough to fill several times per second. Every collection stops all threads, including the one that draws. The nursery is now 32 MB by default, and collections are that much rarer. You can change it in the launcher under memory options.

## The draw thread was waiting for the update thread

On desktop, when no new frame is ready, the draw thread waits up to 100 ms for one. In a browser that wait makes it miss several screen refreshes in a row. The web build does not wait: if nothing new is ready, the refresh is skipped and the next one is tried.

## The game logic was running far too often

With no limit, the update loop rebuilt the whole scene close to a thousand times per second for a screen that shows sixty. It is now capped at 240 per second by default, which leaves input just as responsive and frees a processor core for everything else. The launcher still lets you pick another cap.

## Images were decoded by slow code

Covers, avatars and backgrounds went through a decoder running as interpreted code. Every panel full of images cost a burst of work and garbage. Decoding is now done by a native decoder compiled to WebAssembly.

## What you can do on your side

- Check that hardware acceleration is on. The launcher warns you when the browser draws on the processor.
- Leave frame sync on the default unless you know your screen and browser well.
- The in-game performance graph is on `Ctrl+Alt+P`.

Stutters are not gone on every machine. Measurements keep coming in, grouped by build and by graphics card, and the launcher's Status tab shows them as they are, good or bad.
