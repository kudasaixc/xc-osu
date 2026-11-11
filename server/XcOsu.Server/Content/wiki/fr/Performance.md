# Performances

Comment obtenir des images fluides, et comment trouver ce qui cloche quand ce n'est pas le cas.

## Première vérification : le navigateur utilise-t-il ta carte graphique ?

C'est la cause la plus fréquente d'un jeu injouable. Si le launcher affiche un avertissement sur le rendu logiciel, le navigateur dessine chaque image avec le processeur.

**Chrome, Edge, Brave, Opera**

1. Ouvre les paramètres du navigateur, section *Système*, et active *Utiliser l'accélération graphique si disponible*. Redémarre le navigateur.
2. Ouvre `chrome://gpu` (ou `edge://gpu`). Vers le haut, *WebGL2* doit indiquer *Hardware accelerated*.
3. Si ce n'est pas le cas, mets à jour le pilote de ta carte graphique, puis regarde la liste *Problems detected* de cette page.

**Firefox**

Ouvre `about:support` et regarde la section *Graphics*. La ligne *WebGL 2 Driver Renderer* doit nommer ta carte graphique.

Sur un portable à deux puces graphiques, vérifie dans les réglages graphiques du système que le navigateur a le droit d'utiliser la plus puissante.

## Les options du launcher qui comptent

Les options du launcher s'appliquent au prochain lancement.

| Option | Ce qu'elle fait | Quand la changer |
|---|---|---|
| Synchronisation d'image | La façon dont le jeu cadence ses images. | Garde d'abord le réglage par défaut. Essaie *VSync* en cas de déchirement ou de mouvement irrégulier. |
| Fréquence de mise à jour | Combien de fois par seconde tourne la logique du jeu. Le plafond par défaut est de 240. | À baisser sur un processeur faible. |
| Échelle de rendu | Dessine dans une résolution plus basse et étire le résultat. | À baisser sur une puce graphique faible ou un écran à très haute résolution. |
| Carte graphique | Demande au navigateur la puce rapide ou la puce économe. | Portables à deux puces. |
| Mémoire | Limites et tailles de la mémoire du moteur. | Seulement si tu sais pourquoi. Les valeurs par défaut sont celles qui ont été mesurées. |

## Mesurer

- `Ctrl+Alt+P` affiche un graphique de performances en direct dans le jeu.
- `Ctrl+Alt+L` affiche la console du moteur.
- L'onglet *État* du launcher montre les mesures reçues des joueurs, regroupées par version et par matériel.

## Ce qui fait du mal

- Beaucoup d'onglets ouverts, ou une vidéo qui tourne dans un autre onglet.
- Une fenêtre de navigateur à cheval sur deux écrans de fréquences différentes.
- Les extensions qui s'injectent dans toutes les pages. Essaie un profil sans extensions.
- Les modes d'économie de batterie, qui plafonnent la cadence du navigateur.

## Les premières secondes sont plus lentes

Juste après le démarrage, le moteur compile encore des shaders et charge des ressources. Juge les performances après un petit moment dans le menu, et à la deuxième partie sur une beatmap.
