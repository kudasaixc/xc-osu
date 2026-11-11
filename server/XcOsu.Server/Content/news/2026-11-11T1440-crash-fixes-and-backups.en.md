# Crash fixes and a backup of your data

This update comes straight from the crash reports the game has sent since yesterday.

## Beatmaps with a video no longer crash the game

Starting a beatmap that contains a video stopped the game while it was loading. xc-osu! cannot play videos yet, and it handled that badly. The map now starts normally, without the video. Storyboards are still shown.

## Export and re-import your data

Everything xc-osu! keeps in your browser (beatmaps, skins, local scores, settings, login) now fits in a single file. In the launcher, open **Options**:

- **Export data** creates a `.xcbak` file.
- **Import data** restores a `.xcbak` file. It replaces what the browser holds; the file is checked in full before anything is erased.

Use it to move to another browser or computer, or before clearing the site's data. Keep the file private: it contains your login.

## Everything else

- Mouse sensitivity applies as soon as the game enters fullscreen. Until now you had to leave fullscreen and come back.
- In chat, sender names stay visible when you come back to the game.
- The online players list shows up right away instead of after two to three seconds.
- New accounts get a new default avatar.
- The launcher has a link to [the website](https://osu.xcsoftworks.com/web/) at the bottom of its rail.

## Still open

Two memory-related crashes (in song select and during an import) are not explained yet. The game now reports the measurement that was missing to understand them.
