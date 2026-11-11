# Les sons de frappe arrivent plus tôt

Dans un jeu de rythme, le délai entre l'appui sur une touche et le son de frappe se ressent avant de pouvoir se nommer. Sur la machine de test (Chrome sous Windows, sortie en 48 kHz), ce délai allait de 85 à 112 ms, et pouvait grimper à environ 165 ms après quelques coupures audio. Il est maintenant d'environ 63 ms.

## Où passait le temps

| Étape | Avant | Maintenant |
|---|---|---|
| De l'appui à la demande de lecture | 6–7 ms | 6–7 ms |
| De la demande au premier bloc mixé | 14–26 ms | 3–10 ms |
| Tampon du mixeur | 10–16 ms, jusqu'à 85 ms après des coupures | contourné |
| Tampon du navigateur et périphérique audio | environ 50 ms | environ 50 ms |

La dernière ligne appartient au navigateur et au système d'exploitation. Une page web ne peut pas la raccourcir, quoi qu'elle demande. Tout ce qui est au-dessus était de notre ressort.

## Ce qui a changé

**Les sons courts ne passent plus par le mixeur.** Un son de frappe sans effet est maintenant confié directement au thread audio du navigateur, qui le mélange dans le bloc qu'il est en train de produire. Il ne fait plus la queue derrière la musique.

**Le tampon rétrécit après une coupure.** Il grossissait à chaque accroc audio sans jamais revenir, si bien qu'un mauvais moment en début de session retardait tout jusqu'au rechargement. Il grossit maintenant quand il le faut et retrouve sa taille normale après quelques secondes sans accroc.

**L'horloge de la musique est réglée sur une mesure.** Le jeu doit savoir de combien le son que tu entends est en retard sur le son qu'il a produit. Ce chiffre était estimé, et l'estimation était fausse d'environ 8 ms : l'horloge de la musique était légèrement en retard. Il est maintenant mesuré en continu à partir de l'horloge de sortie du navigateur.

## Ce que ça ne règle pas

Un casque Bluetooth ajoute son propre retard, souvent plus de 100 ms, et rien dans la page ne peut l'enlever. Utilise un casque filaire ou des enceintes, et sers-toi du réglage de décalage du jeu si les notes te semblent encore en avance ou en retard.

Les chiffres ci-dessus viennent d'un seul ordinateur. Les tiens seront différents, surtout sur la dernière ligne du tableau.
