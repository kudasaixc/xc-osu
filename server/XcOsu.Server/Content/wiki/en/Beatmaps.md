# Beatmaps

## Getting beatmaps from inside the game

Open the beatmap listing from the main menu. Type to search, pick a status and a sort order, and click the download button on a beatmap. It is imported as soon as the download ends.

Search understands filters:

| Filter | Meaning |
|---|---|
| `stars>=6` | Star rating of at least 6 |
| `ar>9` | Approach rate above 9 |
| `length<3m` | Shorter than three minutes |
| `bpm>=180` | 180 BPM or faster |
| `status=ranked` | Only ranked beatmaps |
| `creator=name` | By this mapper |
| `"exact phrase"` | Words together, in this order |

## From the website

Every beatmap page on the [website](https://osu.xcsoftworks.com/web/beatmapsets) has a download button for the `.osz` file. Drop that file onto the game's page to import it.

## Importing your own files

Drop `.osz` (beatmaps), `.osk` (skins) or `.osr` (replays) files anywhere on the game's page.

## Where beatmaps come from

xc-osu! does not host beatmap files. They are fetched from public beatmap mirrors. If one mirror is down the game tries another, so a download that fails once is worth retrying a minute later.

## Which beatmaps give pp

Scores are saved on any beatmap the server can identify. Performance points are given on ranked and approved beatmaps, as usual.

## Storage

Beatmaps are kept in your browser's storage for this site. Browsers may clear a site's storage when the disk is nearly full. To free space yourself, delete beatmaps from the game's song select.
