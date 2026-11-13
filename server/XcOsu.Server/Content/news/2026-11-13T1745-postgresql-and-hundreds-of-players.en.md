# PostgreSQL and hundreds of simultaneous players

## A database that no longer queues

Everything the server stores (accounts, scores, beatmaps, chat, teams, notifications) used to live in two SQLite files. SQLite takes one write at a time: with hundreds of players submitting scores simultaneously, requests waited in line behind each other.

The server database now runs on PostgreSQL, on the same machine. Writes no longer queue, and the pages that are read the most (profiles, rankings, beatmaps) are cached instead of being rebuilt on every visit.

## What you notice in game

Profiles open faster, and searching for the same beatmap as everyone else no longer means waiting: the server fetches it once and serves it from its own copy.

## Nothing lost

Every account, score and beatmap was carried over, and the old files are kept aside in case anything needs checking. Your account and your data work as before.
