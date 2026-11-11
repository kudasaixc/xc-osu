# Chat, récupération du compte et profils BBCode

## Le chat reste ouvert quand un lien prend trop de temps à analyser

Un rapport d’erreur a montré que l’ouverture du chat pouvait échouer pendant la détection des liens dans l’historique. Si cette analyse dépasse son délai, le jeu conserve maintenant le message en texte lisible au lieu d’interrompre le jeu.

## Un vrai parcours « mot de passe oublié »

Le lien **Mot de passe oublié ?** dans le jeu et sur le site ouvre un formulaire de récupération par e-mail. Indique l’adresse de ton compte xc-osu!, puis vérifie ta boîte de réception et les indésirables. Le lien est valable 25 minutes et une seule fois. Après le choix du nouveau mot de passe, les anciennes sessions sont révoquées : reconnecte-toi dans le jeu et sur le site.

Les messages partent du même transport mail local que LCW Sportsbook, avec l’expéditeur noreply@xcsoftworks.com. Si le transport est indisponible, la page de récupération l’indique. Réinitialiser ton mot de passe xc-osu! ne change pas ton compte osu! officiel.

## Ton profil garde sa mise en forme dans le jeu

La page **me!** en jeu lit désormais le BBCode au lieu d’afficher les balises prises en charge en texte brut. Gras, italique, souligné, barré, couleurs hexadécimales et tailles de texte sont pris en charge, y compris les styles imbriqués. Les liens et images fonctionnent aussi. Les pages Markdown existantes restent compatibles ; les balises BBCode inconnues ou non fermées restent lisibles en texte brut.

## Des messages plus clairs sur le serveur et les modes de jeu

Les messages de connexion rappellent que les scores éligibles sont envoyés au serveur privé xc osu, sans modifier les classements officiels osu!. Les beatmaps mania, taiko et catch non prises en charge sont bloquées avec une explication. Le mode pris en charge reste osu!standard.
