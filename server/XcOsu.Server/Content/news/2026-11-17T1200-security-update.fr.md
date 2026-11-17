# Une grosse mise à jour de sécurité avant l'ouverture mondiale

Avant d'ouvrir xc-osu! aux joueurs du monde entier, tout le service (le serveur, le site, la page du jeu et la machine qui les fait tourner) est passé par une revue de sécurité complète. Cette mise à jour en est le résultat : des dizaines de corrections, invisibles pour la plupart, et quelques nouveautés que tu peux utiliser.

## Double authentification

Tu peux maintenant protéger ton compte avec un deuxième facteur : un code à 6 chiffres d'une application d'authentification (Aegis, Google Authenticator, 1Password, Bitwarden…). Active-la dans les [réglages de ton compte](/home/account/edit#two-factor) sur le site. Ensuite, la connexion, dans le jeu ou sur le site, demande le code après ton mot de passe. Le jeu avait déjà l'écran pour ça.

Tes réglages listent aussi chaque **appareil connecté**, et tu peux déconnecter celui que tu veux, ou tous d'un coup.

## Des limites plus justes, une connexion plus stable

Les limites qui protègent la connexion, l'inscription et le reste du service voyaient quelques gros relais réseau au lieu de chaque joueur : beaucoup de gens pouvaient partager une même limite. Elles s'appliquent maintenant à la connexion de chaque joueur. Quelqu'un qui se trompe sans arrêt avec ton pseudo ne peut plus te bloquer, et un moment chargé ne se transforme plus en « Identifiants incorrects » pour tout le monde.

Les sessions renouvellent maintenant leur accès toutes les heures en arrière-plan, et un jeton de connexion copié puis réutilisé ferme sa session.

## Mots de passe

Les nouveaux mots de passe sont comparés aux fuites de données connues avec [Have I Been Pwned](https://haveibeenpwned.com/Passwords). Seuls les 5 premiers caractères d'une empreinte du mot de passe quittent le serveur ; rien d'autre, et rien sur ton compte.

## Ton âge

Le chat, les commentaires, les équipes et les salons multijoueur demandent maintenant ton **année de naissance**, une seule fois. xc-osu! est réservé aux joueurs de 13 ans et plus, et avant 16 ans les messages privés sont limités aux amis. Seule l'année est gardée.

## Scores classés

Un score classé compte maintenant une fois son replay rejoué et conforme à ce qui a été envoyé, en général en moins d'une minute : en attendant, il s'affiche sans pp. Le serveur calcule lui-même le grade et la précision à partir des notes touchées. Les replays sont vérifiés par un service séparé qui n'a accès à rien d'autre, et un replay qui n'a pas pu être vérifié est réessayé, puis examiné par une personne au lieu de compter quand même.

## Modération

L'équipe a maintenant de vrais outils pour traiter les signalements, mettre un compte en silence ou le restreindre, retirer des messages et des commentaires, et examiner les scores suspects. Chaque action est enregistrée.

## Vie privée

- Les images des textes de profil passent par le serveur : celui qui les héberge ne voit pas qui regarde un profil.
- Les diagnostics ne relient plus tes sessions entre elles.
- Supprimer ton compte retire aussi ton nom des notifications des autres joueurs.

La [page vie privée](/wiki/Privacy) est à jour avec tout ça. Si tu trouves un problème de sécurité, signale-le en privé : [contact sécurité](https://github.com/kudasaixc/xc-osu/security/advisories/new).
