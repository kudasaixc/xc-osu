# Password recovery

The server uses `/usr/sbin/sendmail`, like LCW Sportsbook, with `noreply@xcsoftworks.com` as the sender. No shared application secrets are needed. `Mail__SendmailPath`, `Mail__From` and `PublicUrl` override the defaults. `PublicUrl` must be a trusted HTTPS origin (default `https://osu.xcsoftworks.com`), never a request Host header.

For an SMTP relay instead, set `Mail__Host`, `Mail__Port` (default 587), `Mail__Username`, `Mail__Password` and `Mail__From`. STARTTLS is required. Keep credentials outside Git.

The server migration creates `password_resets`; only SHA-256 token hashes are stored. Links expire in 25 minutes, can be used once, and are invalidated by a successful password change. A reset revokes all existing access/refresh tokens. Requests return the same response for existing and unknown addresses. Rate limits allow five recovery requests/completions per IP per 15 minutes and at most one email per account per minute. Delivery errors log no recipients or tokens.

Deploy the updated server as well as the web frontend and browser game. Building only the WebAssembly client does not install the recovery API or its database migration. On systems without sendmail or SMTP, the recovery page reports that email recovery is unavailable. A running transport must also be configured to deliver outgoing mail; transport availability alone cannot guarantee inbox delivery.

Automated tests must use an isolated data directory and a capture transport; never send recovery emails to real users as part of validation.
