# Comment un jeu de rythme de bureau tient dans un onglet

lazer a été écrit pour des ordinateurs de bureau. Il lui faut plusieurs threads, une carte graphique à laquelle parler directement, une bibliothèque audio native et une base de données sur disque. Un onglet de navigateur n'offre rien de tout ça tel quel. Voici ce qu'il a fallu reconstruire en dessous, en laissant le code du jeu intact partout où c'était possible.

## L'affichage

Le jeu dessine avec OpenGL ES 3. Dans un navigateur, cela veut dire WebGL2, assez proche pour garder le moteur de rendu d'origine : la couche qu'il appelle a été remplacée par une autre qui transmet au contexte graphique du navigateur. Les shaders, c'est une autre histoire. Le jeu compile ses shaders au démarrage avec une bibliothèque native ; elle a donc été compilée en WebAssembly elle aussi, et son résultat est mis en cache.

## Les threads

Le thread principal d'un navigateur ne doit jamais bloquer, et le jeu bloque en permanence, par conception. Le thread principal de la page ne fait donc presque rien. Le jeu tourne dans des workers : un qui dessine dans le canvas, un qui fait tourner la logique, un qui mixe le son, un qui lit les entrées. Cela demande une page « isolée cross-origin », ce qui explique les en-têtes de sécurité stricts de xc-osu! et l'impossibilité d'y intégrer du contenu venu de n'importe où.

## Le son

Le jeu de bureau utilise BASS, une bibliothèque audio fermée qui n'existe pas pour le web. xc-osu! embarque son propre remplaçant, **xcbass** : la même interface de programmation, écrite en C, avec ses décodeurs et son traitement du tempo et de la hauteur. Il mixe dans un bloc de mémoire partagé avec le thread audio du navigateur, qui le joue.

## La base de données

Les beatmaps, les scores et les réglages vivent dans Realm, une vraie base de données avec des fichiers, des verrous et de la mémoire mappée. Plutôt que d'écrire un sosie, le moteur Realm d'origine a été compilé en WebAssembly et on lui a donné un petit système de fichiers qui se comporte comme il l'attend. Ce que tu as dans le navigateur est le même format de base que sur ordinateur.

## Le stockage

Tout est conservé dans le stockage privé du navigateur pour ce site. Effacer les données du site dans ton navigateur supprime tes beatmaps locales et tes réglages ; ton compte, tes scores et tes classements sont sur le serveur et ne sont pas touchés.

Voilà les fondations. Tout le travail depuis consiste à rendre l'ensemble rapide et complet.
