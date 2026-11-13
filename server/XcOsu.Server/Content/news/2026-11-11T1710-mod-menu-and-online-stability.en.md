# Mod menu, browsing and online stability

## A mod menu that no longer hitches

Opening the mod menu could freeze the game while it built every panel at once. Panels now appear one by one, and searching and selecting stay usable while the menu fills.

Beatmap cards load the same way, one at a time, and a new search cancels the cards that have not appeared yet.

## Disconnections

Refreshing the friends list while the connection was closing could raise an error. It is ignored now.

## Error reports

Some fatal errors were being dropped and network timings could come out negative. Both are fixed, so the next reports will be more useful.

One crash during player loading remains unexplained. The reports we get from now on will help find it.
