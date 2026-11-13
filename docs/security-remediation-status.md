# État des corrections de sécurité — 13 novembre 2026

Ce document décrit ce qui est corrigé, ce qui est vérifié par des tests, et ce qui ne l'est pas. Il ne constitue pas une certification.

## Validation exécutée

Toutes les suites tournent sur un serveur et une base temporaires ; aucun mail réel n'est envoyé à un joueur pendant les tests (transport de capture).

- `tools/tests/security/Security.csproj` : 83 contrôles (authentification, rotation et révocation des sessions, liaison session/WebSocket, catégories et budgets de débit, longueur des mots de passe, confirmation d'adresse, proxy, vérification des replays, pagination des équipes, médias, salons, score tokens).
- `tools/tests/security/review_live.mjs` : 36 contrôles de bout en bout sur un serveur réel, un par point de la revue : coupure du WebSocket après révocation et continuité après rotation, 501 des endpoints de vérification, bornes du mot de passe, budgets derrière une même adresse, DELETE compté, actions réservées aux adresses confirmées, redirections du proxy, pagination des équipes.
- `tools/tests/security/http_smoke.py`, `backup_crypto.mjs`, `tools/password-recovery-test.mjs`, test compte/chat/profil.
- `tools/web-test.mjs` : parcours complet du site dans un navigateur, dans les deux modes (`Accounts:RequireConfirmedEmail` appliqué, avec confirmation par le mail capturé ; et désactivé).
- Livraison réelle d'un mail par le Postfix local (boîte locale du serveur), puis confirmation par le lien reçu.
- Vérificateur de replays essayé sur une copie des données de production : voir plus bas.

## Points de la revue post-fusion

- **A — Révocation WebSocket.** Chaque connexion SignalR est liée à sa session (`SessionConnections`). Révoquer une session, changer ou réinitialiser le mot de passe, ou restreindre le compte coupe les connexions correspondantes. Une rotation autorisée du refresh token transfère la liaison : la connexion en cours continue.
- **B — Endpoints de vérification.** `/api/v2/session/verify`, `/reissue` et `/mail-fallback` répondent 501 : le serveur n'a pas de second facteur et ne prétend plus en avoir.
- **C — Mot de passe.** 8 à 256 caractères partout : formulaires, endpoints, service (`Accounts.PASSWORD_MIN/MAX`).
- **D — Limites par adresse.** Sur `/oauth/token`, les connexions par mot de passe (20/min par adresse) sont distinguées des renouvellements (600/min, 30 échecs/min). Une requête authentifiée dépense le budget de son compte ; l'adresse n'a qu'un plafond 24 fois plus large. Inscription, mot de passe et connexion restent strictement par adresse.
- **E — DELETE.** Compté comme écriture. Les opérations qui touchent quelqu'un d'autre (amis, blocages, suppression de messages ou de commentaires, exclusion d'un salon ou d'une équipe) ont leur propre budget, plus serré.
- **F — Adresse confirmée.** `Accounts:RequireConfirmedEmail` (activé par défaut) réserve aux adresses confirmées les actions visibles par les autres ; la connexion du client natif et le jeu ne sont pas bloqués. La récupération du mot de passe n'exige pas une adresse confirmée : le lien n'arrive qu'à l'adresse du compte, donc son usage la confirme.
- **G — Redirections du proxy.** Une redirection n'est suivie que vers un hôte autorisé ou de la même famille (`.ppy.sh`, `.catboy.best`, `.nerinyan.moe`, `.osu.direct`, plus `Proxy:RedirectHosts`), revalidé à chaque saut.
- **H — Équipes.** La liste est paginée côté site (50 par page) ; plus aucune équipe n'est inaccessible.

Aussi corrigé en passant : le pays, redevenu modifiable depuis le profil par un commit ultérieur, est à nouveau fixé à l'inscription (page et serveur).

## Vérification des replays (points 43, 44, 46)

`server/XcOsu.ReplayVerifier` est branché au serveur (`Services/ReplayVerification.cs`). Chaque replay osu!standard est rejoué hors du client ; si le score, la précision ou le combo annoncés dépassent nettement ce que le replay produit, le score perd son classement et ses pp.

- Configuration : `ReplayVerification:Mode` (`enforce` par défaut, `observe`, `off`), `ReplayVerification:Path` (par défaut `../verifier/XcOsu.ReplayVerifier.dll` à côté du serveur), `ReplayVerification:TimeoutSeconds` (900), `ReplayVerification:RequireReplay` (vrai) et `ReplayVerification:ReplayGraceSeconds` (900) : un nouveau score classé dont le replay n'arrive pas dans ce délai est déclassé.
- Les scores antérieurs sans replay ne sont pas touchés ; les replays déjà stockés sont vérifiés au démarrage.
- Tolérance : 3 % + 2000 de score, 1,5 point de précision, 10 % + 5 de combo. La même partie rejouée ne donne pas exactement le même nombre (spinners, suivi des sliders) ; l'écart mesuré sur de vraies parties est d'au plus 0,7 % de score. Une triche qui reste dans cette marge n'est pas détectée.
- Essai sur une copie de la production : les 21 replays authentiques sont vérifiés, deux scores falsifiés pour l'essai sont déclassés.
- Limites : osu!standard uniquement (taiko, catch et mania restent « non vérifiables » et gardent leur classement). Si le vérificateur n'est pas installé, plante ou dépasse le délai, le score est conservé (échec ouvert) et l'événement est journalisé. La vérification prouve que le replay produit le score ; elle ne prouve pas qu'un humain a joué le replay.

## Ce qui n'a pas été validé

- Une partie WASM complète jouée par une personne, une vraie partie multijoueur à plusieurs navigateurs, et une rotation de session au milieu d'une partie : couvertes par les tests du serveur et du site, pas par une partie réelle.
- La réception d'un mail dans une boîte externe (Gmail, Outlook) : seule la livraison locale est prouvée. La délivrabilité dépend du DNS (SPF/DKIM) du domaine.
- Le client osu!lazer de bureau non modifié face aux changements de session.

## Points encore ouverts

- **38** : le parcours d'inscription immédiate permet encore de distinguer certaines disponibilités.
- **72** : `script-src` et les autres directives de chargement de la CSP restent en observation (import map en ligne générée à chaque build, script en ligne de `settings.html`) ; les appliquer exige des empreintes par build et des essais WASM/workers.
- **12** : pas de reconstruction historique des appartenances aux playlists acquises avant correction.
- **29** : pas de parseur multipart bornant chaque partie avant matérialisation.
- **47** : cohérence exacte début/fin/durée du replay non démontrée.
- **51** : annulation du calcul de pp non préemptive.
- **55** : création des salons temps réel pas encore transactionnelle sur tout son cycle.
- **67** : pas de file de sortie bornée par socket.
- **71** : les données de connexion du jeu dans Realm peuvent figurer dans une sauvegarde.
- **75** : l'import d'une sauvegarde n'est pas transactionnel sur toute la base IndexedDB.
- **40** : le pays initial dépend de l'en-tête géographique du reverse proxy.
