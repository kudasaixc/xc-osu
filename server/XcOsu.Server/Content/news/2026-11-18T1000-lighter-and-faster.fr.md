# xc-osu! plus léger et plus rapide

Cette mise à jour ne change rien à ce que tu vois en jeu : elle change la façon dont le jeu tourne dans ton navigateur.

## Moins de mémoire

Jusqu'ici, toutes les textures, tous les sons et toutes les musiques intégrés au jeu (environ 130 Mo) étaient chargés dans la mémoire du jeu au démarrage, qu'ils servent ou non. Ils sont maintenant rangés dans un fichier à part, gardé en cache par ton navigateur, et le jeu ne lit que ce dont il a besoin, au moment où il en a besoin. Dans nos tests, la mémoire utilisée par le jeu baisse d'environ 11 à 20 %, et le menu principal arrive plus vite.

## Plus de code compilé à l'avance

Une partie du code du jeu tournait encore en mode interprété, plus lent. Grâce à un profil de ce que le jeu exécute vraiment, il ne reste plus que quelques centaines de fonctions interprétées au lieu de près de 4000. Dans nos mesures, le temps de calcul de chaque image baisse, et les pauses de nettoyage de la mémoire pendant une partie sont environ un tiers plus courtes.

## Deux nouveautés dans le lanceur

- **Fréquence du thread audio** : 1000 Hz par défaut. Sur une petite machine, 500 Hz libère du processeur et ne retarde un son de frappe que d'1 ms au plus.
- Si ta dernière session a eu du mal à suivre, ou si ta machine est modeste, le lanceur te **propose le preset « Petite config »**. Tu peux le refuser, il ne te le redemandera pas.

Les chiffres viennent de nos mesures en conditions contrôlées : chez toi, le gain dépend de ta machine. Si quelque chose te semble moins fluide qu'avant, dis-le nous.
