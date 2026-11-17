# Vie privée

Ce que xc-osu! conserve à ton sujet, dit simplement.

## Ton compte

Ton nom d'utilisateur, ton adresse e-mail, une empreinte de ton mot de passe (pas le mot de passe lui-même), ton pays, ton année de naissance, et ce que tu choisis de mettre sur ton profil. Tes scores, tes replays, ta liste d'amis, ton équipe, tes commentaires et tes messages de chat. Si tu actives la double authentification, le secret que ton application d'authentification partage avec le serveur.

Ton année de naissance (l'année seulement) est demandée une fois, avant le chat, les commentaires et les autres fonctions sociales. xc-osu! est réservé aux joueurs de 13 ans ou plus ; avant 16 ans, les messages privés sont limités aux amis. C'est ce que prévoient les règles sur les données des mineurs (RGPD, article 8 ; COPPA).

Ton adresse e-mail n'est pas montrée aux autres joueurs. Elle sert seulement à confirmer ton compte et à envoyer les liens de réinitialisation du mot de passe, par le système d'envoi de courrier du serveur lui-même.

Ces données sont gardées tant que ton compte existe. Elles sont nécessaires pour fournir les fonctions en ligne du jeu (RGPD, article 6.1.b : exécution du service auquel tu t'es inscrit).

## Sessions et journaux du serveur

Quand tu te connectes, le serveur garde une session qui expire au bout de 90 jours au plus, ou dès que tu te déconnectes ou changes ton mot de passe. Tu peux voir tes appareils connectés et déconnecter n'importe lequel dans les [Réglages](/home/account/edit#sessions).

Les événements de sécurité de ton compte (changement de mot de passe ou d'e-mail, double authentification activée ou coupée, sessions fermées, décisions de modération, suppression du compte) sont inscrits dans un journal de sécurité avec l'heure et une forme raccourcie de l'adresse qui a fait la demande. Il est gardé un an, pour enquêter sur les piratages de comptes et pouvoir déclarer une violation de données. Il fait partie de l'export de tes données. Quand un compte est supprimé, l'adresse et le détail de ses entrées sont effacés. Son serveur web enregistre l'adresse qui a fait chaque requête, la page demandée et l'heure, pour traiter les abus et les erreurs. Ces journaux sont supprimés au bout de 14 jours. Le contenu des requêtes, les mots de passe et les jetons ne sont pas enregistrés.

## Le stockage de ton navigateur

Les beatmaps, les skins, les réglages et la base locale du jeu sont enregistrés par ton navigateur sur ton appareil. Ils ne sont pas envoyés.

## Les diagnostics

Activés par défaut, désactivables dans les options du launcher ou dans les [Réglages](/home/account/edit#telemetry). Les désactiver supprime aussi ce qui attendait d'être envoyé.

**Envoyé :** la durée des images, les pauses de nettoyage mémoire, le nom de ta carte graphique et de ton navigateur, la taille et la fréquence de l'écran, la durée du démarrage, l'écran du jeu qui était ouvert, les messages d'erreur du moteur et, après un plantage, les dernières lignes du journal du moteur. Chaque session est indépendante : rien ne relie entre elles les sessions d'un même navigateur.

**Pas envoyé :** le chat, les informations du compte, le contenu des beatmaps, ce que tu tapes.

Les diagnostics reposent sur l'intérêt légitime de xc-osu! à corriger les plantages et les saccades (RGPD, article 6.1.f), c'est pourquoi tu peux les couper à tout moment.

Les messages sont raccourcis et débarrassés des adresses, des e-mails et des jetons dans ton navigateur avant l'envoi. Le serveur n'enregistre pas ton adresse IP avec les diagnostics. Les diagnostics sont supprimés au bout de 45 jours.

## Les miroirs de beatmaps

Les recherches et les téléchargements de beatmaps sont faits par le serveur de xc-osu! à ta place. Les pochettes et les extraits audio sont chargés directement par ton navigateur auprès des hôtes qui les servent.

Les images que les joueurs mettent dans le texte de leur profil sont récupérées par le serveur de xc-osu!, pas par ton navigateur : celui qui les héberge ne sait pas qui regarde un profil.

## Vérification des mots de passe

Quand tu choisis un mot de passe, le serveur vérifie qu'il ne fait pas partie des mots de passe déjà divulgués, avec le service [Have I Been Pwned](https://haveibeenpwned.com/Passwords). Seuls les 5 premiers caractères d'une empreinte (SHA-1) du mot de passe sont envoyés ; ni le mot de passe, ni son empreinte complète, ni rien sur ton compte.

## Les serveurs officiels d'osu!

xc-osu! n'envoie ni ton compte, ni tes scores, ni ton activité aux serveurs officiels d'osu!.

## Tes droits

Tu peux, à tout moment et sans te justifier :

- **Obtenir une copie de tes données.** Dans les [Réglages](/home/account/edit#data), *Télécharger mes données* te donne tout ce qui est gardé sur ton compte (profil, scores, commentaires, messages, amis…) dans un fichier JSON réutilisable ailleurs.
- **Les corriger.** Ton profil et ton mot de passe se modifient dans les Réglages. Pour changer ton adresse e-mail, demande à kudasaixc (voir plus bas).
- **Supprimer ton compte.** Dans les Réglages, *Supprimer mon compte* efface ton compte, tes scores, replays, captures, amis, conversations privées et les messages que tu as envoyés, immédiatement. Un commentaire qui a reçu des réponses reste en place sous la forme d'une entrée vide « supprimé » pour que la discussion garde son sens. Les sauvegardes quotidiennes de la base gardées sur le serveur disparaissent sous 7 jours.
- **T'opposer aux diagnostics** en les coupant dans le launcher.

Pour toute autre demande (une question, une limitation, une demande que tu ne peux pas faire toi-même), envoie un message privé à **kudasaixc**, qui gère xc-osu! (xc softworks). Les demandes reçoivent une réponse sous un mois.

Si tu penses que tes données sont mal traitées, tu peux aussi porter plainte auprès de l'autorité de protection des données. En France, c'est la [CNIL](https://www.cnil.fr/fr/plaintes).
