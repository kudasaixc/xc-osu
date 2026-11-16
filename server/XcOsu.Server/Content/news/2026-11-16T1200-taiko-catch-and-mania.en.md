# osu!taiko, osu!catch and osu!mania are here

## Four modes in the browser

The game used to ship only the osu! ruleset. It now carries all four: **osu!**, **osu!taiko**, **osu!catch** and **osu!mania**. Pick the mode from the toolbar like in lazer. Song select lists each beatmap under its own mode, and an osu! beatmap can also be played as a convert in the three other modes.

## Your existing beatmaps

Beatmaps you imported before this update were all filed as osu! beatmaps, because the other rulesets weren't there yet to recognise them. On first launch the game reads every beatmap file once more to find its real mode, then recomputes its stars in the background. This happens once and you don't need to do anything. The stars may show a dash for a short while.

## Scores, pp and rankings

The server computes star rating and pp for every mode, converts included. Each mode has its own global and country rankings, its own statistics on your profile and its own beatmap leaderboards. A score is refused if its beatmap can't be played in that mode: only osu! beatmaps convert.

Replay verification works in every mode too, so taiko, catch and mania scores go through the same checks as osu! scores.

## Medals

Each mode gets new medals: a pass and a full combo for every star level from 1★ to 8★, and milestones for the total number of hits. Easy, No Fail and Half Time don't count for the star medals.

## On the website

The website names the four modes the way osu! does and puts an icon next to each one. A beatmap page shows the settings each mode actually uses: the number of keys for mania, OD and HP only for taiko. Score pages name hits the way each mode does (Max/300/200… in mania, fruits, drops and droplets in catch).

Multiplayer quick play stays osu!-only for now, like upstream.
