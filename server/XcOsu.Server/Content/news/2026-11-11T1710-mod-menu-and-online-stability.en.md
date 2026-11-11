# Mod menu, browsing and online stability

## Progressive mod panels and beatmap cards

This update targets hitches when opening the browser mod menu. Preloaded panels now join the interface progressively: each column attaches one panel per update frame instead of finalising its entire list at once. Selection and search remain available as panels appear.

Beatmap browsing uses the same approach, attaching one card per frame for search results, subsequent pages and card-size changes. A new search cancels cards still waiting to appear so results from different searches cannot mix.

This bounds the number of new interface trees finalised in one frame. Actual hitch reduction still needs measurement on the new build, especially on the first opening; garbage-collection pauses and the cost of an individual panel remain possible.

## Cleaner disconnection

A friends refresh could race with connection teardown and raise a SignalR error. The client now ignores refreshes against a disposed connection and removes its friends-list subscription when disposed.

## More reliable error reports

A fatal error immediately following a suppressed repeated warning is now retained in telemetry. Resource timing no longer produces negative network durations when the browser supplies incomplete timing data.

Today's reports also include an exit during player loading with only a runtime finalizer stack. That trace cannot identify its cause, and this update does not claim to fix that particular exit. Future reports will help continue the investigation.
