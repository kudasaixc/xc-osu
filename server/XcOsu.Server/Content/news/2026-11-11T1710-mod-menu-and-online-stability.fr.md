# Menu des mods, navigation et stabilité en ligne

## Un menu des mods sans à-coups

L’ouverture du menu des mods dans le navigateur pouvait figer le jeu un instant : toutes les tuiles étaient construites et finalisées d’un coup. Les tuiles préchargées rejoignent maintenant l’interface progressivement, une par frame, et la liste reste parcourable pendant qu’elle se remplit.

La liste des beatmaps fait pareil avec ses cartes, et une nouvelle recherche annule celles qui attendent encore, pour que les résultats de deux recherches ne se mélangent pas. Le gain réel reste à mesurer sur un build neuf : une pause du ramasse-miettes ou le coût d’une tuile lourde peut encore se voir.

## Déconnexions plus propres

Rafraîchir la liste d’amis pendant la fermeture de la connexion pouvait déclencher une erreur SignalR. Les rafraîchissements sur une connexion déjà détruite sont ignorés, et l’abonnement est détaché en même temps que la connexion.

## Rapports d’erreur

Une erreur fatale juste après un avertissement répétitif filtré était perdue. Elle est gardée désormais. Les durées réseau ne sortent plus en négatif quand le navigateur fournit des informations de timing incomplètes.

Un arrêt pendant le chargement d’une partie est arrivé avec une pile qui ne montre que le finalizer du runtime. Celui-là reste inexpliqué, et cette mise à jour ne prétend pas le contraire.
