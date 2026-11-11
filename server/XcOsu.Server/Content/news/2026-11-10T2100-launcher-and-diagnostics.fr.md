# Un launcher, un préréglage iPhone, et ce que les diagnostics envoient

Ouvrir xc-osu! ne te jette plus directement devant une barre de chargement. Il y a maintenant un launcher : une page qui s'affiche tout de suite, pendant que le moteur se télécharge derrière.

## Le launcher

Le launcher présente les nouvelles, l'état du portage et les problèmes connus. C'est aussi là que se trouvent les réglages à choisir avant le démarrage du jeu : synchronisation d'image, fréquence de mise à jour, résolution de rendu, limites de mémoire, choix de la carte graphique, latence audio. Ils s'appliquent au prochain lancement. Si tu préfères ne pas le voir, coche la case pour le passer ; ajouter `?launcher` à l'adresse le fait revenir.

## iPhone et iPad

Un téléphone ou une tablette a bien moins de mémoire à accorder à un onglet qu'un ordinateur. Le préréglage iPhone / iPad est appliqué automatiquement sur ces appareils : résolution de rendu réduite de moitié, moins de workers en réserve, cible de mémoire plus basse, pas de plein écran automatique. Il augmente les chances que le jeu démarre. Il ne fait pas d'un iPhone un moyen confortable de jouer, et certains appareils ferment encore l'onglet pour excès de mémoire.

## Les diagnostics

Le jeu envoie des mesures de performance, et tu peux le désactiver dans les options du launcher. Puisque c'est activé par défaut, voici exactement ce que ça couvre.

**Ce qui est envoyé :** la durée des images et le nombre d'images en retard, les pauses dues au nettoyage de la mémoire, le nom de ta carte graphique et de ton navigateur, la taille de l'écran, le temps de démarrage du jeu, l'écran du jeu sur lequel tu te trouvais, et les messages d'erreur du moteur. Quand le jeu plante ou que l'onglet meurt, les dernières lignes du journal du moteur.

**Ce qui ne l'est pas :** ton chat, ton compte, tes beatmaps, ce que tu tapes. Les messages sont raccourcis et débarrassés des adresses, des jetons et des e-mails avant de quitter ton navigateur. Le serveur n'enregistre pas ton adresse IP avec ces mesures, et elles sont supprimées au bout de 45 jours.

**À quoi ça sert :** savoir quelles cartes graphiques et quels navigateurs saccadent, et si une nouvelle version a amélioré ou dégradé les choses. L'onglet État du launcher montre le résultat, regroupé par version et par matériel.

Deux raccourcis vont avec : `Ctrl+Alt+P` affiche le graphique de performances en jeu, `Ctrl+Alt+L` affiche la console du moteur.
