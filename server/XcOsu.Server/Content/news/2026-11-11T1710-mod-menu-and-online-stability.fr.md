# Menu des mods, navigation et stabilité en ligne

## Un affichage progressif des mods et des beatmaps

Cette mise à jour cible les à-coups à l’ouverture du menu des mods dans le navigateur. Les tuiles préchargées sont désormais ajoutées progressivement à l’interface : chaque colonne ajoute une tuile par frame, au lieu de finaliser toute sa liste en une seule fois. La sélection et la recherche restent disponibles pendant l’affichage progressif.

La liste de beatmaps utilise le même principe, avec une carte ajoutée par frame. Cela s’applique aux résultats de recherche, aux pages suivantes et aux changements de taille des cartes. Une nouvelle recherche annule les cartes encore en attente pour éviter de mélanger deux recherches.

Ce changement réduit la quantité de nouvelles interfaces à finaliser dans une même frame. Le gain réel sur les saccades doit encore être mesuré sur le nouveau build, notamment lors de la toute première ouverture ; les pauses du ramasse-miettes et le temps de chargement d’une tuile individuelle restent possibles.

## Une déconnexion plus propre

Un rafraîchissement de la liste d’amis pouvait partir pendant la fermeture de la connexion en ligne et produire une erreur SignalR. Le client ignore maintenant les rafraîchissements sur une connexion déjà détruite et détache son abonnement à la liste d’amis à sa fermeture.

## Des rapports d’erreur plus fiables

Une erreur fatale survenant immédiatement après un avertissement répétitif filtré reste maintenant enregistrée dans la télémétrie. Les durées réseau ne deviennent plus négatives lorsque le navigateur fournit des informations partielles sur une ressource.

L’analyse des rapports du jour a aussi relevé un arrêt pendant le chargement d’une partie, dont la pile ne contient que le finalizer du runtime. Cette trace ne permet pas d’identifier sa cause ; cette mise à jour ne prétend pas corriger cet arrêt précis. Les prochains rapports aideront à poursuivre le diagnostic.
