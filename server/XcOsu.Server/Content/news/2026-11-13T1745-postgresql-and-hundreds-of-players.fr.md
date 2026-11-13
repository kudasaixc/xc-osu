# PostgreSQL et des centaines de joueurs simultanés

## La base quitte SQLite

Tout ce que le serveur sait (comptes, scores, beatmaps, tchat, équipes, notifications, télémétrie) tenait dans deux fichiers SQLite. SQLite sérialise les écritures au niveau du fichier : un seul écrivain à la fois, tous les autres font la queue derrière le verrou. C’était très bien à dix joueurs. Avec des centaines de scores envoyés au même moment, un seul verrou donnait le rythme de tout le serveur.

C’est maintenant PostgreSQL 16, sur la même machine, joignable par le socket local. L’état a été transféré en une seule transaction et les nombres de lignes vérifiés des deux côtés : 36 tables, dont 11 comptes, 137 scores, 3 153 beatmapsets, 13 334 beatmaps et 181 083 événements de télémétrie.

## Ce qui change concrètement

- Les connexions sont regroupées dans un pool (4 à 120) et les requêtes sont préparées automatiquement après trois utilisations.
- Les statistiques de profil sont mises en cache quinze secondes au lieu d’être recalculées à chaque visite.
- Quand plusieurs joueurs ouvrent la même beatmap, le miroir n’est interrogé qu’une fois. Les autres attendent cette réponse, et la réponse sérialisée est gardée dix secondes.
- Les classements, la carte des pays et les équipes ont chacun leur cache, vidé dès qu’une écriture arrive.

## Les chiffres

300 connexions simultanées pendant 15 secondes sur la vraie base : 805 requêtes par seconde, 228 ms de latence médiane, 1,7 s au 99e centile, aucune erreur. Avec 75 clients connectés parmi les 300, 651 requêtes par seconde ; les seules réponses non-200 étaient des tentatives de connexion volontairement rejetées par la limite de débit.

Tout cela dépend de la machine, du réseau et des miroirs de beatmaps, et un premier passage avec des caches froids reste plus lent.
