# Mod menu, browsing and online stability

## The mod menu no longer hitches

Opening the browser mod menu could freeze the game for a moment: every panel was built and finalised at once. Preloaded panels now join the interface progressively, one per update frame, and the list stays searchable while it fills.

Beatmap browsing does the same with its cards, and a new search cancels the cards still waiting to appear, so results from two searches cannot mix. The real gain still has to be measured on a fresh build: a garbage-collection pause or the cost of one heavy panel can still show.

## Cleaner disconnections

A friends-list refresh could race with connection teardown and raise a SignalR error. Refreshes against a disposed connection are ignored now, and the subscription is dropped when the connection is.

## Error reports

A fatal error right after a suppressed repeated warning used to be lost. It is kept now. Resource timing no longer produces negative network durations when the browser returns incomplete timing data.

One exit during player loading came with a trace that only shows the runtime finalizer. That one is still unexplained, and this update does not pretend otherwise.
