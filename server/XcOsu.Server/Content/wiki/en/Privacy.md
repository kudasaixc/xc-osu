# Privacy

What xc-osu! stores about you, in plain words.

## Your account

Your username, e-mail address, a hash of your password (not the password itself), your country, your year of birth, and what you choose to put on your profile. Your scores, replays, friends list, team, comments and chat messages. If you turn on two-factor authentication, the secret your authenticator app shares with the server.

Your year of birth (only the year) is asked once, before chat, comments and other social features. xc-osu! is for players aged 13 or over; under 16, private messages are limited to friends. This follows the rules on children's data (GDPR article 8, COPPA).

Your e-mail address is not shown to other players. It is only used to confirm your account and to send password reset links, through the server's own mail system.

This data is kept for as long as your account exists. It is needed to provide the game's online features (GDPR article 6(1)(b), performance of the service you signed up for).

## Sessions and server logs

When you sign in, the server keeps a session that expires after 90 days at most, or as soon as you sign out or change your password. You can see your signed-in devices and sign any of them out in [Settings](/home/account/edit#sessions).

Security events of your account (password or e-mail changes, two-factor authentication turned on or off, sessions ended, moderation decisions, account deletion) are written to a security log with the time and a shortened form of the address that made the request. It is kept for one year, to investigate account takeovers and to meet the obligation to report data breaches. It is included in your data export. When an account is deleted, the address and details of its entries are erased. Its web server logs the address that made each request, the page requested and the time, to deal with abuse and errors. These logs are deleted after 14 days. Request contents, passwords and tokens are not logged.

## Your browser's storage

Beatmaps, skins, settings and the game's local database are stored by your browser on your device. They are not uploaded.

## Diagnostics

Enabled by default, and can be turned off in the launcher's options or in [Settings](/home/account/edit#telemetry). Turning them off also deletes anything waiting to be sent.

**Sent:** frame timings, memory collection pauses, the name of your graphics card and browser, screen size and refresh rate, start-up duration, which screen of the game was open, engine error messages, and after a crash the last lines of the engine's log. Each session is separate: nothing links the sessions of one browser together.

**Not sent:** chat, account details, beatmap contents, anything you type.

Diagnostics are covered by xc-osu!'s legitimate interest in fixing crashes and stutters (GDPR article 6(1)(f)), which is why you can turn them off at any time.

Messages are shortened and stripped of addresses, e-mails and tokens in your browser before being sent. The server does not store your IP address with diagnostics. Diagnostics are deleted after 45 days.

## Beatmap mirrors

Beatmap searches and downloads are made by the xc-osu! server on your behalf. Cover images and audio previews are loaded directly by your browser from the hosts that serve them.

Images that players put in their profile text are fetched by the xc-osu! server, not by your browser: whoever hosts them does not learn who looks at a profile.

## Password checks

When you choose a password, the server checks it is not a known leaked password, with the [Have I Been Pwned](https://haveibeenpwned.com/Passwords) service. Only the first 5 characters of a fingerprint (SHA-1) of the password are sent; neither the password, nor its full fingerprint, nor anything about your account.

## Official osu! servers

xc-osu! does not send your account, your scores or your activity to the official osu! servers.

## Your rights

You can, at any time and without giving a reason:

- **Get a copy of your data.** In [Settings](/home/account/edit#data), *Download my data* gives you everything kept about your account (profile, scores, comments, messages, friends…) as a JSON file you can reuse elsewhere.
- **Correct it.** Your profile and password can be changed in Settings. To change your e-mail address, ask kudasaixc (see below).
- **Delete your account.** In Settings, *Delete my account* erases your account, scores, replays, screenshots, friends, private conversations and the messages you sent, immediately. A comment that received replies stays in place as an empty "deleted" entry so the conversation still makes sense. Daily database backups kept on the server disappear within 7 days.
- **Object to diagnostics** by turning them off in the launcher.

For anything else (a question, a restriction, a request you cannot make yourself), send a private message to **kudasaixc**, who runs xc-osu! (xc softworks). Requests are answered within one month.

If you think your data is mishandled, you can also complain to your data protection authority. In France, it is the [CNIL](https://www.cnil.fr/fr/plaintes).
