# Chat, account recovery and BBCode profiles

## Chat stays open when a link takes too long to parse

An error report showed that opening chat could fail while detecting links in its history. When link detection exceeds its time budget, the game now keeps the message readable as text instead of interrupting the game.

## A real forgotten-password flow

The **Forgot your password?** link in the game and on the website opens an email recovery form. Enter the email used for your xc-osu! account and check your inbox and spam folder. The reset link lasts 25 minutes and works once. Once you choose a new password, existing sessions are revoked: sign in again in the game and on the website.

Mail is sent through the same local mail transport used by LCW Sportsbook, from noreply@xcsoftworks.com. If the transport is unavailable, the recovery page tells you. Resetting an xc-osu! password does not change your official osu! account.

## Your profile keeps its formatting in the game

The in-game **me!** page now reads BBCode instead of displaying supported tags as raw text. Bold, italic, underline, strikethrough, hexadecimal colours and text sizes are supported, including nested styles. Links and images work too. Existing Markdown pages still work; unknown or unclosed BBCode stays readable as plain text.

## Clearer server and game-mode messages

Connection messages explain that eligible scores go to the xc osu private server, without changing official osu! rankings. Unsupported mania, taiko and catch beatmaps are blocked with an explanation. osu!standard remains the supported mode.
