# La chasse aux saccades

La première version publique tournait, mais pas régulièrement. Les menus accrochaient, les panneaux en ligne revenaient de quelques pixels en arrière pendant le défilement, et une image figée à peu près chaque seconde rendait les maps rapides pénibles. Voici ce qui en était la cause et ce qui a changé.

## Le ramasse-miettes arrêtait le jeu plusieurs fois par seconde

Le jeu alloue sans cesse de petits objets. Le runtime les ramasse dans une « nursery » qui faisait 4 Mo par défaut, assez peu pour se remplir plusieurs fois par seconde. Chaque collecte arrête tous les threads, y compris celui qui dessine. La nursery fait maintenant 32 Mo par défaut, et les collectes sont d'autant plus rares. Le réglage se trouve dans le launcher, parmi les options de mémoire.

## Le thread de dessin attendait le thread de logique

Sur ordinateur, quand aucune image nouvelle n'est prête, le thread de dessin en attend une pendant 100 ms au plus. Dans un navigateur, cette attente lui fait rater plusieurs rafraîchissements d'écran de suite. La version web n'attend pas : s'il n'y a rien de neuf, le rafraîchissement est sauté et on tente le suivant.

## La logique du jeu tournait beaucoup trop souvent

Sans limite, la boucle de mise à jour reconstruisait toute la scène près de mille fois par seconde pour un écran qui en affiche soixante. Elle est maintenant plafonnée à 240 par seconde par défaut, ce qui garde des entrées aussi réactives et libère un cœur de processeur pour le reste. Le launcher permet toujours de choisir un autre plafond.

## Les images étaient décodées par du code lent

Les pochettes, les avatars et les fonds passaient par un décodeur exécuté en code interprété. Chaque panneau rempli d'images coûtait une rafale de travail et de déchets. Le décodage est maintenant fait par un décodeur natif compilé en WebAssembly.

## Ce que tu peux faire de ton côté

- Vérifie que l'accélération matérielle est activée. Le launcher te prévient quand le navigateur dessine avec le processeur.
- Laisse la synchronisation d'image sur le réglage par défaut, sauf si tu connais bien ton écran et ton navigateur.
- Le graphique de performances en jeu s'affiche avec `Ctrl+Alt+P`.

Les saccades n'ont pas disparu sur toutes les machines. Les mesures continuent d'arriver, regroupées par version et par carte graphique, et l'onglet État du launcher les montre telles qu'elles sont, bonnes ou mauvaises.
