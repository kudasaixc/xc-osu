# État des corrections de sécurité — 12 novembre 2026

Modifications dans les sources uniquement. Aucun déploiement ni migration de la base de production effectué. Ce document ne constitue pas une certification « 75/75 » ou « zéro régression ».

## Validation exécutée

- `tools/tests/security/Security.csproj` : 46 contrôles réussis (authentification, migrations des tokens, images, confirmation email et récupération, adresses réseau du proxy, autorisations du chat, quotas de connexions, en-têtes transférés, transaction des salons et concurrence des score tokens).
- `tools/tests/security/http_smoke.py` : serveur et base temporaires ; inscription, connexion native/navigateur, cookies, révocation, commande locale d'administration, filtrage du proxy et sessions de télémétrie.
- `tools/tests/security/backup_crypto.mjs` : compatibilité v1, chiffrement v2, mauvais mot de passe, altération, troncature, données ajoutées, limites des fichiers.
- Vérification syntaxique de huit modules JavaScript modifiés.
- Pas de test complet dans un navigateur du moteur WASM, d'une partie multijoueur ou d'une livraison réelle d'email.

## Modifications principales

- Authentification : suppression du premier administrateur automatique ; commande locale `grant-admin <id>` ; expiration et rotation des refresh tokens avec durée absolue ; empreintes en base et migration des sessions existantes ; restriction de compte centralisée ; révocation lors du changement/reset de mot de passe ; taille et concurrence PBKDF2 bornées ; quotas et temporisation progressive des échecs.
- Email : file bornée, liens à usage unique et expiration, confirmation et récupération avec `/usr/sbin/sendmail`, comme sportsbook. Les tests désactivent les envois. La récupération exige une adresse confirmée.
- Navigateur : sessions HttpOnly/Secure/SameSite ; compatibilité OAuth native conservée. Les faux endpoints de second facteur renvoient une indisponibilité explicite.
- WebSockets : contrôles de salon pour expulsion/chat, suppression des anciens groupes d'amis, quotas d'observation et de connexions, propriétaire de session spectateur, validation précoce du score token, bornes des frames, délais d'envoi et revalidation des tokens.
- Proxy : DNS public contrôlé à la connexion, redirections revalidées, octets effectivement plafonnés, contenus actifs forcés en téléchargement, clé de cache par URL, quota et nettoyage périodique, fichiers temporaires nettoyés et nombre de verrous borné.
- Médias : décodage/réencodage, suppression des métadonnées, limites de dimensions/animations/concurrence, écritures atomiques ; animations autorisées préservées dans les limites.
- Scores : checksums obligatoires pour l'éligibilité, restriction revérifiée à l'écriture, collections et dates bornées, tokens plafonnés atomiquement, critères d'éligibilité partagés, calculs avec concurrence/file/cache bornés, téléchargements `.osu` plafonnés et atomiques.
- Communauté : playlists et durées bornées, création transactionnelle des salons de playlists, recherches liées au salon, équipes paginées et capacité atomique, commentaires bornés et parents contrôlés, quotas de messages/signalements et déduplication des signalements.
- Télémétrie : session anonyme signée, contrôles d'origine, quotas et file d'ingestion bornée. Une session anonyme ne prouve pas la véracité des événements.
- Sauvegardes : avertissement, chiffrement facultatif AES-GCM en flux, contrôle avant import, limites décompressées/entrées/chemins et contrôle de capacité navigateur.
- Apache : configuration préparée avec `nosniff`, politique de référent, logs sans query string, CSP appliquée pour `object-src`, `base-uri`, `frame-ancestors` et `form-action`, directives de chargement en mode observation. Configuration active non modifiée.

## Points qui ne doivent pas être annoncés comme entièrement résolus

- **43, 44, 46** : pas de simulation indépendante du replay en production ni de conditionnement complet du classement/pp à un replay vérifié. `server/XcOsu.ReplayVerifier` est un prototype hors ligne (osu!standard uniquement, appuyé sur l'infrastructure de test du jeu) : il compile mais n'est ni validé sur des replays réels ni branché au serveur. Les contrôles de plausibilité ne remplacent pas cette vérification. Aucun déclassement global des parties légitimes ajouté pour masquer cette lacune.
- **38** : erreurs d'inscription moins détaillées, mais le parcours inscription immédiate permet encore de distinguer certaines disponibilités. Une protection complète exige une adaptation du parcours de confirmation, y compris côté client natif.
- **72** : seules les directives sans effet sur le chargement sont appliquées. `script-src` et les autres directives de chargement restent en observation : la page du jeu contient une import map en ligne générée à chaque build et `settings.html` un script en ligne ; leur application exige des empreintes par build puis des essais WASM, workers et navigateur.
- **12** : les anciens abonnements temps réel ne donnent plus accès ; les appartenances persistées aux playlists asynchrones restent reconnues. Pas de reconstruction historique des appartenances éventuellement acquises avant correction.
- **29** : limites HTTP/multipart et champs présentes ; pas de parseur multipart dédié bornant chaque partie avant toute matérialisation.
- **47** : fenêtres temporelles contrôlées, mais cohérence exacte début/fin/durée effective du replay non démontrée.
- **51** : file/cache/concurrence bornés ; annulation du calcul de pp synchrone non préemptive.
- **55** : création des playlists asynchrones transactionnelle ; création temps réel prévalidée mais pas encore transactionnelle sur tout son cycle avec les notifications.
- **67** : délais d'envoi et quotas présents ; pas de file de sortie dédiée bornée par socket.
- **71** : nouveaux tokens du site hors localStorage ; les données de connexion propres au jeu dans Realm restent distinctes et peuvent figurer dans une sauvegarde.
- **75** : contrôle de quota navigateur indicatif ; l'import conserve son comportement de remplacement et n'est pas transactionnel sur toute la base IndexedDB.
- **40** : le chemin de modification du profil présent ne modifie pas le pays. Le pays initial dépend encore de l'en-tête géographique ; sa provenance de confiance doit être garantie par le déploiement.

## Avant mise en production

Valider les parcours réels : connexion/renouvellement du jeu et du site, partie solo avec replay, création/join/kick/chat/multijoueur, spectateur, téléchargements, images et import/export d'une sauvegarde représentative. Sauvegarder les données et vérifier la configuration du reverse proxy et du mail avant déploiement. Les corrections de sécurité introduisent volontairement des refus pour les requêtes expirées, non autorisées ou au-delà des limites.
