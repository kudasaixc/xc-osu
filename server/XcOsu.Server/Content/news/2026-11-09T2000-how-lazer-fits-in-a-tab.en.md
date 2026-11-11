# How a desktop rhythm game fits in a tab

lazer was written for desktop computers. It expects several threads, a graphics card it can talk to directly, a native audio library and a database on disk. A browser tab offers none of that as is. Here is what had to be rebuilt underneath, with the game's own code left as it was wherever possible.

## Drawing

The game renders through OpenGL ES 3. In a browser that means WebGL2, which is close enough for the original renderer to be kept: the layer the renderer calls was replaced by one that forwards to the browser's graphics context. Shaders are a different story. The game compiles its shaders when it starts, with a native library, so that library was compiled to WebAssembly too and its output is cached.

## Threads

A browser's main thread must never block, and the game blocks all the time, by design. So the page's main thread does almost nothing. The game runs on workers: one draws into the canvas, one runs the game logic, one mixes audio, one reads input. This needs a page that is "cross-origin isolated", which is why xc-osu! sends strict security headers and cannot simply embed content from anywhere.

## Sound

The desktop game uses BASS, a closed audio library that does not exist for the web. xc-osu! ships its own replacement, **xcbass**: the same programming interface, written in C, with its own decoders and its own tempo and pitch processing. It mixes into a block of memory shared with the browser's audio thread, which plays it.

## The database

Beatmaps, scores and settings live in Realm, a real database with files, locks and memory mapping. Rather than writing a look-alike, the original Realm engine was compiled to WebAssembly and given a small file system that behaves the way it expects. What you have in the browser is the same database format as on desktop.

## Storage

All of it is kept in the browser's private storage for this site. Clearing the site's data in your browser removes your local beatmaps and settings; your account, scores and rankings are on the server and are not affected.

That is the foundation. Everything since has been about making it fast and making it complete.
