# Performance

How to get smooth frames, and how to tell what is wrong when you do not.

## First check: is the browser using your graphics card?

This is the most common cause of an unplayable game. If the launcher shows a warning about software rendering, the browser is drawing every frame on the processor.

**Chrome, Edge, Brave, Opera**

1. Open the browser's settings, section *System*, and turn on *Use graphics acceleration when available*. Restart the browser.
2. Open `chrome://gpu` (or `edge://gpu`). Near the top, *WebGL2* should read *Hardware accelerated*.
3. If it does not, update your graphics driver, then look at the *Problems detected* list on that page.

**Firefox**

Open `about:support` and look at the *Graphics* section. *WebGL 2 Driver Renderer* should name your graphics card.

On a laptop with two graphics chips, make sure the browser is allowed to use the powerful one, in the operating system's graphics settings.

## Launcher options that matter

The launcher's options apply at the next launch.

| Option | What it does | When to change it |
|---|---|---|
| Frame sync | How the game paces its frames. | Leave on the default first. Try *VSync* if you see tearing or uneven motion. |
| Update rate | How often the game logic runs. The default cap is 240 per second. | Lower it on a weak processor. |
| Render scale | Draws at a lower resolution and stretches the result. | Lower it on a weak graphics chip or a very high-resolution screen. |
| Graphics card | Asks the browser for the fast or the power-saving chip. | Laptops with two chips. |
| Memory | Limits and sizes for the engine's memory. | Only if you know why. The defaults are the measured ones. |

## Measuring

- `Ctrl+Alt+P` shows a live performance graph in game.
- `Ctrl+Alt+L` shows the engine's console.
- The launcher's *Status* tab shows measurements collected from players, grouped by build and hardware.

## Things that hurt

- Many open tabs, or a video playing in another tab.
- A browser window spread across two screens with different refresh rates.
- Browser extensions that inject into every page. Try a profile without extensions.
- Battery saver modes, which cap the browser's frame rate.

## The first seconds are slower

Right after starting, the engine is still compiling shaders and loading resources. Judge performance once you have been in the menu for a little while, and on a second play of a beatmap.
