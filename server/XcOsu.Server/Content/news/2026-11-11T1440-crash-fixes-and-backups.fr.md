# Plantages corrigés et sauvegarde de tes données

Cette mise à jour vient directement des rapports de plantage envoyés par le jeu depuis hier.

## Les beatmaps avec vidéo ne font plus planter le jeu

Lancer une beatmap qui contient une vidéo arrêtait le jeu pendant le chargement. xc-osu! ne sait pas encore lire les vidéos, et il s'y prenait mal pour le dire. La map se lance maintenant normalement, sans la vidéo. Le storyboard, lui, s'affiche toujours.

## Exporter et réimporter tes données

Tout ce que xc-osu! garde dans ton navigateur (beatmaps, skins, scores locaux, réglages, connexion) peut maintenant tenir dans un seul fichier. Dans le launcher, ouvre **Options** :

- **Exporter les données** crée un fichier `.xcbak`.
- **Importer des données** restaure un fichier `.xcbak`. Il remplace ce que le navigateur contient ; le fichier est vérifié en entier avant que quoi que ce soit ne soit effacé.

C'est utile pour changer de navigateur ou d'ordinateur, ou avant de vider les données du site. Garde ce fichier pour toi : il contient ta connexion.

## Le reste

- La sensibilité de la souris s'applique dès l'entrée en plein écran. Il fallait jusqu'ici en sortir et y revenir.
- Dans le chat, le nom des expéditeurs reste affiché quand tu reviens dans le jeu.
- La liste des joueurs en ligne s'affiche tout de suite au lieu d'attendre deux à trois secondes.
- Les nouveaux comptes ont un nouvel avatar par défaut.
- Le launcher a un lien vers [le site](https://osu.xcsoftworks.com/web/) en bas de sa barre.

## Ce qui reste ouvert

Deux plantages liés à la mémoire (à la sélection de map et pendant un import) ne sont pas encore expliqués. Le jeu envoie maintenant la mesure qui manquait pour les comprendre.
