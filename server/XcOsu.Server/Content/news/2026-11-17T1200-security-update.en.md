# A big security update before going worldwide

Before opening xc-osu! to players everywhere, the whole service (the server, the website, the game page and the machine they run on) went through a complete security review. This update is the result: dozens of fixes, most of them invisible, and a few new things you can use.

## Two-factor authentication

You can now protect your account with a second factor: a 6-digit code from an authenticator app (Aegis, Google Authenticator, 1Password, Bitwarden…). Turn it on in your [account settings](/home/account/edit#two-factor) on the website. From then on, signing in, in the game or on the website, asks for the code after your password. The game already had the screen for it.

Your settings also list every **signed-in device**, and you can sign any of them out, or all of them at once.

## Fairer limits, steadier sign-in

The limits that protect sign-in, registration and the rest of the service used to see a handful of large network relays instead of each player, so many people could share one limit. They now apply to each player's own connection. Someone repeatedly failing to sign in with your username can no longer block you, and a busy moment no longer turns into "Incorrect sign in" for everyone.

Sessions now renew their access every hour in the background, and a sign-in token that was copied and reused ends its session.

## Passwords

New passwords are checked against known data breaches with [Have I Been Pwned](https://haveibeenpwned.com/Passwords). Only the first 5 characters of a fingerprint of the password leave the server; nothing else, and nothing about your account.

## Your age

Chat, comments, teams and multiplayer rooms now ask for your **year of birth**, once. xc-osu! is for players aged 13 or over, and under 16 private messages are limited to friends. Only the year is kept.

## Ranked scores

A ranked score now counts once its replay has been played back and matches what was submitted, usually within a minute: until then it shows without pp. The server works out the grade and the accuracy from the hits itself. Replays are checked by a separate service that has no access to anything else, and a replay that cannot be checked is tried again and then looked at by a person instead of counting anyway.

## Moderation

The team now has proper tools to handle reports, silence or restrict accounts, remove messages and comments, and review suspicious scores. Every action is recorded.

## Privacy

- Images in profile texts are loaded through the server, so whoever hosts them cannot see who views a profile.
- Diagnostics no longer link your sessions together.
- Deleting your account also removes your name from other players' notifications.

The [privacy page](/wiki/Privacy) is up to date with all of this. If you find a security problem, please report it privately: [security contact](https://github.com/kudasaixc/xc-osu/security/advisories/new).
