# Sound

## Latency

The delay between a key press and its hit sound has two parts. The game's part is small. The browser's and the sound device's part is usually around 50 ms on a computer with wired output, and a web page cannot reduce it.

What you can do:

- **Use wired headphones or speakers.** Bluetooth adds 100 ms or more.
- In the launcher's audio options, keep latency on *interactive*.
- Close other applications that use the sound device in exclusive or low-latency modes.
- If notes feel consistently early or late, set the audio offset in the game's settings.

## Crackling or dropouts

Crackling means the audio thread did not get to run in time. It usually comes with stutters; see [Performance](/wiki/Performance). In the launcher, the *balanced* or *playback* audio latency settings trade a little delay for fewer dropouts.

## No sound at all

Browsers do not let a page play sound before you have clicked or pressed a key on it. Click once inside the game. If there is still nothing, check that the tab is not muted (right-click the tab) and that the browser is sending sound to the right device.

## Formats

The game plays the audio formats beatmaps normally use: MP3, Ogg Vorbis and WAV.
