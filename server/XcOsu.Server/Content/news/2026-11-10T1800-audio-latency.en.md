# Hit sounds arrive sooner

In a rhythm game, the delay between pressing a key and hearing the hit sound is something you feel before you can name it. On the test machine (Chrome on Windows, 48 kHz output) that delay was between 85 and 112 ms, and could climb to about 165 ms after a few audio dropouts. It is now about 63 ms.

## Where the time went

| Stage | Before | Now |
|---|---|---|
| Key press to play request | 6–7 ms | 6–7 ms |
| Play request to first mixed block | 14–26 ms | 3–10 ms |
| Mixer buffer | 10–16 ms, up to 85 ms after dropouts | bypassed |
| Browser buffer and sound device | about 50 ms | about 50 ms |

The last line belongs to the browser and the operating system. A web page cannot shorten it, whatever it asks for. Everything above it was ours to fix.

## What changed

**Short sounds skip the mixer.** A hit sound with no effect applied is now handed straight to the browser's audio thread, which mixes it into the very block it is producing. It no longer queues behind the music.

**The buffer shrinks again after a dropout.** It used to grow at every audio glitch and never come back, so one bad moment early in a session made everything late until you reloaded. It now grows when needed and returns to its normal size after a few clean seconds.

**The music clock is set from a measurement.** The game needs to know how far the sound you hear is behind the sound it produced. That figure used to be estimated, and the estimate was about 8 ms off, so the music clock ran slightly late. It is now measured continuously from the browser's own output clock.

## What this does not fix

Bluetooth headphones add their own delay, often more than 100 ms, and nothing in the page can remove it. Use wired headphones or speakers, and use the game's offset setting if notes still feel early or late.

The figures above come from one computer. Yours will differ, mostly on the last line of the table.
