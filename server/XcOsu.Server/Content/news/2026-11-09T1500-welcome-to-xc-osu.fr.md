# Bienvenue sur xc-osu!

xc-osu!, c'est le client lazer complet dans un onglet de navigateur. Rien à installer : on ouvre la page, on attend le premier téléchargement, on joue.

Ce n'est ni une réécriture ni un flux vidéo. Le jeu que tu lances est le vrai client, compilé en WebAssembly, qui dessine avec WebGL2 et sort le son par le moteur audio du navigateur. Tes beatmaps, tes skins, tes scores et tes réglages sont enregistrés par le navigateur sur ta machine, et tu les retrouves à ta prochaine visite.

## Ce qui marche aujourd'hui

- **osu! standard**, avec les mods, les replays et les skins.
- **Les beatmaps** : recherche et téléchargement depuis le jeu, ou dépôt d'un fichier `.osz` sur la page.
- **Les comptes** : scores, pp, classements mondial et par pays, amis, chat.
- **Le multijoueur et le mode spectateur**, sur les serveurs de xc-osu!.

## Ce qui ne marche pas encore

- taiko, catch et mania ne sont pas embarqués pour l'instant.
- Les vidéos de fond et de storyboard ne sont pas lues.
- Le premier lancement télécharge beaucoup. Les suivants viennent du cache du navigateur et démarrent bien plus vite.

## Ses propres serveurs

Tout ce qui est en ligne passe par xc-osu!. Les comptes sont créés ici, les scores sont envoyés ici, et les pp sont calculés ici avec les mêmes calculateurs que le jeu. Rien n'est envoyé aux serveurs officiels d'osu!, et un compte créé ici n'existe qu'ici.

## Ce qu'il te faut

Un navigateur récent basé sur Chromium (Chrome, Edge, Brave, Opera) sur un ordinateur : c'est la configuration la mieux testée. Firefox fonctionne, avec moins de recul. L'accélération matérielle doit être activée : sans vraie carte graphique le jeu tourne, mais bien trop lentement pour être jouable.

Amuse-toi, et dis-nous ce qui casse.
