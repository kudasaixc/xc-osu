# PostgreSQL et des centaines de joueurs simultanés

## La base du serveur quitte SQLite

Tout l’état du serveur — comptes, scores, beatmaps, tchat, équipes, notifications, télémétrie — vivait jusqu’ici dans deux fichiers SQLite. SQLite sérialise les écritures au niveau du fichier : quand plusieurs centaines de joueurs jouent et envoient des scores en même temps, les requêtes font la queue derrière un seul verrou d’écriture. La base est maintenant servie par PostgreSQL 16 sur la machine, joignable par le socket local, avec un rôle dédié au compte de service.

L’import a été fait en une seule transaction : chaque table existante a été remplacée par son contenu SQLite, et les nombres de lignes ont été vérifiés — 36 tables, dont 11 comptes, 137 scores, 3 153 beatmapsets, 13 334 beatmaps et 181 083 événements de télémétrie. Les fichiers SQLite sont conservés le temps de valider la migration ; le serveur ne les ouvre plus.

## Ce qui a changé côté performance

- les connexions à la base sont regroupées dans un pool de 4 à 120 connexions, avec préparation automatique des requêtes après trois utilisations ;
- les statistiques de profil sont mises en cache quinze secondes au lieu d’être recalculées à chaque visite ;
- une recherche de beatmaps simultanée ne télécharge plus une fois par joueur la même page du miroir : une seule requête part, les autres attendent son résultat, et la réponse sérialisée est gardée dix secondes ;
- les classements, la carte des pays et les équipes ont leurs propres caches, invalidés à chaque écriture.

## Les chiffres

Sur la machine de production, avec la vraie base PostgreSQL et 300 connexions simultanées pendant 15 secondes : 805 requêtes par seconde, latence médiane 228 ms, 99e centile 1,7 s, aucune erreur, toutes les réponses en HTTP 200. Un essai avec des connexions authentifiées (75 clients connectés parmi 300) tournait à 651 requêtes par seconde ; les seules réponses non-200 étaient les limites volontaires de tentatives de connexion.

Ces chiffres dépendent de la machine, du réseau et des miroirs de beatmaps, et les caches expliquent une bonne partie du gain : un premier passage à froid reste plus lent.

## Exploitation

Le service dépend maintenant de PostgreSQL : le script d’installation crée le rôle `xc-osu` et la base `xc_osu` si nécessaire, et le script de redémarrage refuse de lancer le serveur si PostgreSQL est arrêté. La commande d’import (`import-sqlite`) reste disponible pour restaurer une ancienne sauvegarde SQLite.
