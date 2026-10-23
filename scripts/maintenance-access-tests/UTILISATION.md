# Maintenance MEP

Version du 23 octobre 2026 : **r11**. Manœuvre manuelle et dépose d’actionneur séparées ; raisons d’absence du personnage affichées directement. Sur la copie d’Antibes, 24 vérifications natives Revit 2025 ont réussi pour la 955038 : manœuvre originale, dépose originale, puis dépose avec l’origine à 1 m du vrai sol. Aucun organe manuel fiable n’est identifié sur cette famille. Pour les deux déposes, le retrait de calcul de 700 mm dépasse la portée du mannequin ; cette course reste à confirmer depuis la pièce ou sa notice. Les changements de geste, le mode réel initial et l’affichage des motifs ont aussi été vérifiés. DLL 2023 à 2027 compilées et installées ; identifiant de la DLL 2025 : **23.10.2026-r11 · B88766F8**.

Dans **BIMaestro > Beta > Maintenance MEP**, choisir une intervention. Le menu historique **MEP Booster > Extraction moteur** reste disponible.

## Vanne, chaîne, filtre ou actionneur

1. **Choisir le geste et l’équipement.** Choisir **Ouvrir / fermer une vanne manuellement**, **Déposer un actionneur** ou **Entretenir un filtre**, puis sélectionner l’équipement. La recherche de manœuvre utilise seulement un volant, une chaîne ou un levier réellement identifié ; elle ne bascule jamais vers la dépose d’un moteur, même si une ancienne correction de dépose est mémorisée. Le secours manuel d’une vanne motorisée exige un organe manuel identifié. La commande électrique n’est pas simulée. La fenêtre reste initialement **Dans la maquette**, y compris si aucun repère n’est identifié ; la démonstration reste un choix explicite.
2. **Vérifier la proposition.** Dans **Dans la maquette**, vérifier la pièce reconnue, les éventuels candidats, l’axe et le poste. **Posture à essayer** propose une recherche automatique, debout face à la commande, de profil à gauche / à droite, ou accroupie. Changer ce choix relance le placement sur les sols réels. **Rechercher un poste pour tout le geste…** conserve les cotes courantes et compare les positions ; sans commande reconnue, le bouton propose d’abord de la détecter. **Corriger un repère ou le poste…** donne accès aux corrections manuelles et à la relance complète de l’identification. Les fenêtres se rouvrent après les sélections dans Revit.
3. **Vérifier dans la maquette.** Ouvrir **Cotes du geste et origine des valeurs…** seulement si des dimensions ou leur provenance doivent être précisées. Renseigner la référence de notice si utilisée, confirmer les repères, puis lancer **Vérifier l’intervention** en mode **Dans la maquette**. Un vrai point des pieds est nécessaire. Les autres réglages du mannequin et de la pièce restent repliés. Une modification invalide le résultat précédent ; changer d’intervention efface les anciens repères et remet les dimensions du mécanisme en hypothèses.

### Dans la maquette ou démonstration du geste

Le sélecteur au-dessus de l’aperçu distingue deux usages :

- **Dans la maquette** montre l’équipement et son environnement réels. Un mannequin accepté exige un point des pieds sur un vrai support. Si les positions essayées rencontrent des solides, **Montrer le poste refusé et ses obstacles** affiche un exemple de posture **rouge**, explicitement refusé, ainsi que la géométrie réelle de ses obstacles en rouge. Ce point refusé ne devient jamais un poste accepté et ne permet pas de lancer une validation du poste. Si seule la commande est repérée, le mécanisme peut être animé. Si la commande manque, choisir un candidat ou pointer le bon repère.
- **Démonstration du geste** montre un schéma animé avec un sol et un personnage synthétiques, hors de l’environnement Revit. Elle reste utilisable sans commande identifiée ou sans poste praticable. L’équipement réel et son contexte sont masqués ; le bandeau rappelle en permanence qu’aucun poste de la maquette n’est validé. La démonstration ne renseigne pas les pieds réels et n’est jamais envoyée au scanner de collisions. **Vérifier l’intervention** y est désactivé.

Utiliser **Lire le geste** ou le curseur pour comprendre le mouvement. Les dimensions connues ou hypothétiques du geste peuvent servir à la démonstration ; elles doivent toujours être qualifiées pour le contrôle réel. Basculer de mode invalide l’ancien résultat et ne constitue pas une confirmation des repères.

Sans personnage, le motif est affiché directement sous le résultat : commande non identifiée, aucun sol modélisé à portée, empreinte des pieds non supportée, geste complet hors de portée, corps en collision, géométrie du corps indéterminée ou limite de recherche atteinte. Une recherche interrompue ne démontre pas l’impossibilité du geste. En dépose, le diagnostic de portée indique les hauteurs du point de prise au début et à la fin du retrait par rapport aux appuis testés : abaisser l’origine de la vanne ne suffit pas à rendre tout le retrait atteignable.

### Choisir un repère ou corriger un axe incliné

La détection recoupe géométrie, sous-composants, paramètres et raccordements ; le titre de la famille seul ne prouve pas la fonction d’une pièce circulaire. Si plusieurs pièces restent plausibles, une liste **Repère 1**, **Repère 2**, etc. permet de les examiner. En mode **Dans la maquette**, le candidat sélectionné est repéré par un cercle orange et un axe violet. **Utiliser le repère surligné…** applique ce choix puis recherche un poste au sol. Ce choix utilisateur reste à confirmer ; il ne prouve pas qu’un cercle est un volant ou un capot de maintenance.

Dans **Corriger un repère ou le poste…**, **Pointer la commande / le capot…** corrige le centre. Pour une branche de filtre ou une commande oblique, **Définir un axe incliné par deux points…** permet de pointer le centre sur l’équipement, puis un second point du modèle dans le sens voulu. Cette direction n’est pas limitée aux axes locaux X, Y et Z. Vérifier la flèche violette et utiliser **Inverser le sens** si nécessaire. La définition du repère relance la recherche du poste ; elle ne donne pas les dimensions ou la course de la pièce à retirer.

Pour un levier, **Pointer l’extrémité de la poignée…** corrige sa direction initiale et sa longueur depuis le pivot déjà renseigné. Le repère garde l’axe courant ; le point choisi donne la direction de prise dans son plan de rotation. Vérifier la hauteur du pivot et le sens de rotation. Le gabarit de poignée est une section carrée conservatrice, incluant une partie derrière le pivot et l’encombrement du coude quand celui-ci est mesuré ; il ne reproduit pas exactement la tôle et les doigts.

### Mémoriser une correction

Dans **Corriger un repère ou le poste…**, **Mémoriser ces réglages pour les équipements compatibles** enregistre explicitement le repère, les cotes et les hypothèses du geste. Cette action ne demande pas de déclarer une intervention validée. Elle est disponible dans la maquette réelle, avec une commande renseignée ; les repères fictifs de la démonstration ne peuvent pas être mémorisés.

À une prochaine ouverture, la correction est recherchée automatiquement après la reconnaissance. Elle n’est réutilisée que si les traits géométriques, raccordements et paramètres déterminants correspondent, y compris dans un autre projet. Le repère est enregistré dans les coordonnées locales de l’équipement, puis transformé selon sa position, sa rotation et son éventuel miroir. Une variante incompatible, une lecture incomplète ou une ambiguïté ne reprend pas silencieusement les anciens réglages. La fenêtre indique la réutilisation ou le motif du refus.

Les pieds, le sol, la posture et les résultats de collision ne sont pas enregistrés : ils sont recalculés dans la maquette courante. Les hypothèses et l’origine déclarée des cotes restent visibles ; une correction mémorisée ne devient ni une mesure constructeur ni une confirmation automatique. **Oublier la correction mémorisée pour cet équipement** supprime la correspondance pour le geste courant. La mémoire reste locale au poste utilisateur, entre les projets et les sessions Revit.

### Ce qui peut être reconnu et ce qui reste à renseigner

| Intervention | Proposition automatique | Compléments nécessaires selon la famille |
| --- | --- | --- |
| Volant manuel | Centre, axe et rayon à partir de cercles / faces coaxiales, recoupés avec les paramètres et les raccordements. Le rayon de prise et la section sont distingués lorsque la géométrie le permet. | Confirmer qu’il s’agit du volant et son sens. Si la section de couronne est inconnue, le tracé fin de 2 mm est seulement une représentation provisoire ; régler la section et le rayon réels. L’angle proposé reste réglable. |
| Volant avec chaîne | Roue, puis descente et écartement si deux brins modélisés ou des paramètres concordants sont exploitables. | Renseigner les brins manquants, la course de traction et la hauteur de prise réelle. Vérifier la boucle, les guides et la place des mains. |
| Levier manuel | Recherche dédiée d’une poignée allongée reliée à un axe de tige, recoupée avec les raccordements et les cotes disponibles. Si une correspondance unique est exploitable, pivot, axe, direction initiale et encombrement sont proposés. | Les formes ouvertes et fermées d’une famille ne doivent pas devenir deux poignées simultanées. Les cas ambigus restent à choisir ou corriger. La course proposée de 90° est une hypothèse explicite : confirmer le sens, les butées et le mouvement réel. |
| Filtre | Face extérieure et axe d’un capot circulaire plausible ; sens proposé à partir de la branche et des raccordements. | Un capot reconnu ne donne pas le volume du tamis / de la cartouche. Diamètre et longueur ne sont repris que si des paramètres correspondants existent. Vérifier position intérieure, sens, volume et course de retrait. |
| Actionneur | Sous-composant explicite, géométrie isolée et repère de montage concordant ; gabarit mesuré sur ce composant. | Sans composant exploitable, définir manuellement le volume et les repères. La course, les fixations, les câbles, l’accouplement et la démontabilité restent à confirmer. |

Si plusieurs pièces sont aussi plausibles, aucun centre n’est imposé automatiquement : choisir le candidat après surlignage ou pointer la bonne pièce. La détection d’un capot ou d’un actionneur n’invente pas une course constructeur. Les dimensions et courses initiales qui restent dans les champs sont des **gabarits hypothétiques de visualisation**, à remplacer ou qualifier avant de conclure.

**Cotes mesurées dans la famille** signifie géométrie ou paramètres du modèle. Cela ne transforme pas les autres champs en mesures et ne remplace pas une notice de maintenance. **Cotes reportées d’une notice constructeur** est une origine déclarée par l’utilisateur : l’outil ne télécharge ni ne vérifie la notice. Respecter le point de départ exact d’une cote de retrait sur le dessin constructeur.

### Poste de l’opérateur et chaîne

Le poste est recherché sur une surface de sol modélisée exploitable : plancher, palier, escalier, rampe ou terrain, y compris dans un lien direct chargé. Chaque chaussure est échantillonnée séparément, dans l’orientation du mannequin ; aucun appui fictif n’est exigé dans l’espace vide entre les deux pieds. La portée et le corps sont également contrôlés. Pour une dépose accompagnée, les appuis sont aussi vérifiés le long du déplacement horizontal. Un tuyau, un équipement ou un massif d’équipement ne devient pas un sol parce que sa face supérieure est horizontale. La sélection manuelle refuse ces objets comme appui.

La recherche automatique essaie plusieurs orientations du corps debout puis accroupi. De profil, le mannequin tourne réellement ; il ne devient pas plus mince. Accroupi, les genoux se fléchissent, les hanches et les épaules s’abaissent et le torse s’incline ; les longueurs des membres restent conservées. Les composants du corps et leur recul sont contrôlés avec des volumes orientés et des balayages natifs, plutôt qu’un pavé global. Ce modèle simplifié ne simule pas l’équilibre, la fatigue, les appuis des mains, les équipements de protection ni les capacités d’une personne donnée.

La proposition de poste vérifie les appuis, la portée et le mouvement complet : corps, bras, mains et mécanisme ou pièce retirée. Un poste où le corps tient mais dont les bras rencontrent un voisin est comparé à d’autres positions et orientations. Les obstacles du mécanisme ou du retrait qui restent identiques quel que soit le poste sont signalés séparément : changer la posture ne les résout pas. Une recherche limitée ne démontre pas l’absence de solution. **Vérifier l’intervention** relance le contrôle avec les réglages actuels. Les enveloppes conservatrices peuvent signaler un voisin proche ; elles ne constituent pas une simulation biomécanique d’un intervenant.

Les anciennes valeurs de 650 × 650 mm deviennent un **rectangle de confort indicatif**, visible en gris avec les volumes ; elles ne bloquent pas le corps et ne représentent pas des minima réglementaires. Une posture géométriquement possible peut rester contraignante. L’outil le signale séparément ; il ne déduit pas « non conforme » d’un passage de 30, 65 ou 80 cm. La notice produit et une évaluation de l’intervention réelle restent nécessaires.

Si seul le grand encombrement d’un import CAD sans géométrie exploitable recoupe le poste, l’outil peut montrer un **personnage provisoire en ambre** sur le vrai sol : ses appuis, sa portée et les solides connus restent contrôlés, mais l’import n’est pas déclaré libre. L’avertissement de géométrie inconnue reste présent et interdit un résultat « Libre géométriquement ». Un solide ou maillage qui présente une collision réelle, ainsi qu’un équipement inconnu, continuent à bloquer le poste. Cette proposition dans la maquette se distingue de la démonstration sur sol fictif.

Au lancement du contrôle, si les appuis ou la posture ne sont plus praticables avec les réglages courants, le poste est refusé. Un exemple rouge peut expliquer la collision ; aucun mannequin vert n’est conservé comme poste valide. La démonstration hors maquette reste disponible. Rechercher une autre position ou prévoir un moyen d’accès réel. La résistance, l’état du revêtement, la stabilité de l’intervenant et le trajet d’approche ne sont pas validés par cet échantillonnage.

Les repères de projet et données spatiales ne sont pas des obstacles physiques. Un import CAD propre à une vue, ou reconnu intégralement comme courbes / polylignes horizontales d’épaisseur inférieure à 1 mm, est traité comme un dessin de référence. Un import 3D, une surface, un maillage, une lecture partielle ou une qualification dépassant le budget restent signalés ; le nom du DWG ne permet jamais de l’écarter.

Pour une chaîne, une **hauteur de prise / sol** peut être suggérée à partir du bas de boucle, du dégagement des mains et de la course de traction. C’est une cote géométrique réglable, pas une hauteur de travail recommandée par une norme. Vérifier qu’elle correspond à la manipulation réelle ; modifier la prise ou la chaîne puis relancer le contrôle. Une boucle au niveau ou sous le sol, une orientation de roue incohérente ou une portée insuffisante entraîne un refus ou un avertissement.

Le bas de boucle n’impose pas la hauteur des mains : la prise peut se faire plus haut sur les brins libres. Une recherche automatique peut essayer une autre hauteur atteignable avant de proposer une posture contraignante. En validation, la cote de prise explicitement renseignée est conservée.

### Lire l’aperçu

Les volumes contrôlés sont **masqués par défaut** pour garder l’équipement et le mannequin lisibles. Activer **Afficher les volumes contrôlés** pour voir les dégagements du geste affiché. En démonstration, ces volumes sont illustratifs ; ils ne sont pas un résultat de contrôle de la maquette. Les enveloppes répétées sont échantillonnées par groupe pour garder la vue lisible ; le scanner analyse séparément l’ensemble des volumes du contrôle réel. Une éventuelle limite supplémentaire de l’aperçu est indiquée, avec priorité au corps et aux conflits rouges.

Glisser pour tourner la vue, utiliser la molette pour zoomer et **Recentrer** pour retrouver l’ensemble de l’intervention. Dans la maquette, l’équipement est bleu, l’opérateur vert, la chaîne dorée, l’axe violet et les obstacles rouges. En démonstration, le petit plancher est fictif.

La lecture prépare les 49 postures une fois pour les réglages courants, puis interpole ces mêmes postures à chaque image. Sa durée suit le temps écoulé ; une cadence d’affichage réduite ne ralentit plus artificiellement le geste. Pause, reprise et déplacement du curseur gardent la position choisie. Les volumes contrôlés utilisent la même séquence préparée que le mannequin animé. Une modification des réglages reconstruit cette séquence.

Le pilote de lecture s’adapte à la cadence mesurée au début du geste, puis conserve ce choix pendant la pause et la reprise. Les modifications du profil ou de l’affichage des volumes relancent cette sélection. Le rendu logiciel imposé par certains hôtes Revit peut encore limiter la cadence des familles détaillées.

Le lissage des contours est temporairement réduit pendant la lecture, la rotation et le zoom, puis rétabli à l’arrêt. Le bloc de contexte reste affiché. La navigation utilise une surface de souris 2D et les tracés fixes sont regroupés pour réduire le coût du dessin.

Le contexte réel est une **boîte de coupe** englobant l’équipement, le mouvement et le poste accepté ou refusé, avec **500 mm de marge sur chaque face**. Les surfaces des murs, sols et réseaux sont coupées aux limites de ce bloc. Les éléments bloquants sont lus en priorité et leur géométrie capturée est colorée en rouge. Le budget de capture et les éventuelles omissions restent indiqués. Modifier le geste au-delà du bloc initial est signalé ; rouvrir la fenêtre recapture le contexte. Le scanner analyse séparément les volumes contre le modèle et les liens directs chargés. Les cadres rouges complémentaires restent des boîtes d’obstacles, pas des surfaces exactes de contact. Tourner, zoomer, animer et recolorer réutilisent les géométries capturées.

Les volumes de bras et de mains couvrent les 49 postures interpolées de l’animation simplifiée. Le balayage du levier est conservateur entre ses orientations. Une chaîne possède deux brins sous gravité et une prise accessible sous la roue. Pour le filtre / l’actionneur, la dépose est une translation du pavé renseigné ; le mannequin accompagne son déplacement horizontal.

Le volume du corps est contrôlé contre l’équipement choisi. Les volumes d’action, de retrait et de bras excluent la famille et ses sous-composants, car le modèle ne distingue pas automatiquement les interfaces de contact et les pièces internes. Cette limite est indiquée dans chaque résultat. Les pièces et espaces Revit sont des données spatiales et ne sont pas des obstacles physiques.

## Moteur et portes

Choisir la porte finale, puis **Trouver le chemin jusqu’à la sortie**. L’option **Portes intermédiaires supposées ouvertes** est activée initialement : d’autres portes peuvent être traversées. Les murs et leurs ouvertures restent contrôlés ; les dimensions nominales disponibles ajoutent un contrôle du volume complet dans le portail. Le vantail ouvert, le dormant et l’ouverture effective restent à confirmer. Une porte hébergée dans un mur sans cotes fiables peut être franchie par son trou, mais le résultat est marqué incomplet ; sans mur hôte ni dimensions, elle reste un obstacle.

Les virages et translations sont contrôlés à hauteur constante ; l’outil ne simule ni chariot, ni levage, ni basculement. Le moteur est supposé désaccouplé de la pompe. Un échec de recherche ne démontre pas qu’aucun chemin physique n’existe.

## Sens du résultat et références

- **Obstacle détecté** : un solide pénètre un volume conservateur de l’intervention. Le rôle concerné apparaît dans les détails.
- **À confirmer** : aucun obstacle trouvé, mais hypothèses non qualifiées, repères non confirmés, portée dépassée, géométrie manquante ou avertissements non résolus. Une proposition automatique ne constitue pas à elle seule une validation.
- **Libre géométriquement** : aucun obstacle dans les données analysées, repères déclarés confirmés, origine des cotes renseignée, appuis vérifiés et absence d’avertissement restant dans le contrôle. Ce libellé ne valide pas une procédure ou une conformité réglementaire.

Les valeurs initiales non mesurées et les proportions du mannequin sont des hypothèses réglables. Il n’existe pas ici de dégagement universel valable pour chaque vanne ou filtre. Aucune cote anthropométrique, aucun couple admissible et aucune conformité normative ne sont inventés. Les prescriptions produit doivent venir de sa notice, en respectant l’origine exacte des cotes.

- [INRS — Dix points clés de conception](https://www.inrs.fr/demarche/conception-lieux-situations-travail/dix-points-cles.html) : accès et volumes d’intervention.
- [INRS — Prévention en maintenance](https://www.inrs.fr/risques/maintenance/prevention.html) : préparation et évaluation selon les conditions réelles, les moyens nécessaires et les risques de l’intervention ; la seule absence de collision ne suffit pas.
- [Rotork — Chainwheels](https://www.rotork.com/en/products/gearbox-accessories/chainwheels), notice PUB148-015 (juin 2023) : guides et boucle continue de chaîne.
- [Spirax Sarco — Fig 34HP TI-P168-01](https://www.spiraxsarco.com/-/media/spiraxsarco/international/documents/en/ti/fig_34hp-ti-p168-01-en.ashx) : cotes de retrait propres au produit / DN.
- [SAMSON — EB8015](https://www.samsongroup.com/document/e80150en.pdf), chapitre 5 : accès et dépose.
- [ISO 14738:2002 — domaine](https://www.iso.org/standard/27556.html) exclut les besoins spécifiques de maintenance ; [ISO 11226 — domaine](https://www.iso.org/standard/25573.html) ne suffit pas à valider un effort de manœuvre.

L’effort, la masse, la consignation, les outils réels, la stabilité du sol, le chemin d’approche du personnel, les températures et les moyens de levage restent à qualifier. Aucun objet ou paramètre n’est modifié dans le projet. Les réglages restent temporaires tant que l’utilisateur n’emploie pas explicitement **Mémoriser ces réglages** ; cette mémoire est enregistrée séparément de la maquette.
