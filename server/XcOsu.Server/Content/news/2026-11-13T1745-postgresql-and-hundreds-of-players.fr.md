# PostgreSQL et des centaines de joueurs simultanés

## Une base qui ne fait plus la queue

Tout ce que le serveur stocke (comptes, scores, beatmaps, tchat, équipes, notifications) tenait dans deux fichiers SQLite. SQLite n’accepte qu’une écriture à la fois : avec des centaines de joueurs qui envoient leurs scores en même temps, les requêtes attendaient leur tour.

La base tourne maintenant sur PostgreSQL, sur la même machine. Les écritures ne se bloquent plus, et les pages les plus consultées (profils, classements, beatmaps) sont mises en cache au lieu d’être recalculées à chaque visite.

## Ce que tu remarques en jeu

Les profils s’ouvrent plus vite, et chercher la même beatmap que tout le monde ne veut plus dire attendre : le serveur la récupère une fois et la sert depuis sa propre copie.

## Rien de perdu

Tous les comptes, scores et beatmaps ont été transférés, et les anciens fichiers sont gardés de côté au cas où. Ton compte et tes données fonctionnent comme avant.
