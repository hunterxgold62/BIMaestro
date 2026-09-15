# MEP Booster — v6 : copie d’orientation

## Copie d’orientation : utilisation et limites

Sélectionner UN accessoire de référence, ouvrir la rosace puis cliquer sur **Copier l’orientation…**.
Le bouton est masqué pour une sélection multiple. Sélectionner ensuite un ou plusieurs accessoires
cibles de la même famille puis **Terminer**. La référence est exclue des cibles et reste inchangée.
L’orientation ET le sens de montage sont toujours copiés, sans case à cocher. Vérifier les silhouettes vertes
dans la vue puis confirmer avec OK ; Annuler ou Échap ne modifie rien.

- Première version limitée aux accessoires droits à deux connecteurs alignés, de la même famille, non hébergés et non en miroir. Pas encore de copie pour tés/coudes ; leurs rotations habituelles restent disponibles.
- Orientation autour de chaque axe de tuyau, référencée à la verticale du projet ; axe Y de repli pour les tuyaux presque verticaux (|Z| > 0,99). Le résultat peut différer du sens apparent à l’écran : vérifier l’aperçu, particulièrement près du seuil vertical.
- Le sens de montage est inclus systématiquement : le repère des extrémités est aligné dans le sens positif de l’axe principal du projet. Ce n’est pas une lecture ni une copie du sens hydraulique. Les types dont les repères de connecteurs diffèrent sont refusés.
- Le lot est regroupé en une seule opération Annuler. Une incompatibilité ou un échec provoque un retour arrière de tout le lot. Les contrôles de raccordement et de stabilité des voisins sont réutilisés à chaque rotation/inversion.
- Aucun changement avant confirmation. Sans contours disponibles pour toutes les cibles, la copie est refusée au lieu de proposer un aperçu incomplet.
- La pastille est suspendue pendant le choix des cibles et la confirmation, puis réarmée sans devoir resélectionner.

État actuel de l’interface : réapparition après 200 ms de repos, fond de rosace à 8 % d’opacité,
rotations ±30/45/60/90/180 et angle personnalisé signé (−180 à +180, non nul).
Les sections v5/v4 ci-dessous décrivent l’historique et leurs anciens délais.

### Validation manuelle requise dans Revit 2023 et 2025

1. Une vanne source puis plusieurs cibles de même famille sur tuyaux parallèles : Annuler puis recommencer avec OK. Vérifier que la source reste inchangée.
2. Plusieurs vannes orientées différemment : résultat conforme aux silhouettes ; un seul Ctrl+Z rétablit tout le lot.
3. Tuyaux perpendiculaires, inclinés et verticaux : vérifier le repère relatif et les silhouettes avant application.
4. Référence montée à l’envers : vérifier que le sens est reproduit automatiquement ; contrôler les connexions et les tuyaux voisins.
5. Familles différentes, élément miroir/hébergé, té/coude : pipette indisponible ou refus explicite sans modification.
6. Échap pendant le choix des cibles et Annuler à la confirmation : aucune modification et retour de la pastille.

Tests locaux : mathématiques de rotation signée/repère vertical/axes obliques, options de la pipette,
disponibilité et rendu WPF. Les tests locaux ne lancent pas de transactions sur un vrai document Revit.

Bouton **MEP Booster ON/OFF** dans le panneau **Beta** du ruban BIMaestro.
L’état est OFF au démarrage de Revit ; aucun observateur souris ne reste actif lorsque OFF.

## Changements v5

- Pas de lecture répétée de la sélection lorsque la rosace est masquée et la sélection inchangée. Contrôle de vue plafonné à une fois toutes les 150 ms lorsque visible. Infobulle du ruban mise à jour seulement si le texte change.
- Aucun chargement de contours à la sélection. Chargement et mise en cache au premier survol, au plus 80 arêtes / 900 points par pièce et 5 000 points au total. Une seule géométrie WPF de contours par pièce au lieu d’un contrôle par arête.
- Observateur souris natif installé uniquement pendant l’affichage de la pastille/rosace, retiré à sa fermeture. Journal normal limité à une écriture toutes les deux secondes, erreurs conservées immédiatement.
- Thème partagé `Themes/BIMaestroTheme.xaml` : pastille verte de marque, rosace blanche, texte sombre, boutons secondaires et couleurs de survol de la charte.
- Axe principal du té détecté à partir des deux connecteurs alignés. Le coude pivote sur son unique extrémité raccordée, ou sur la première si aucune n’est raccordée. Inversion autour de la branche du té ou de la bissectrice du coude.
- Les actions sont évaluées avant affichage : tous les points raccordés doivent pouvoir être conservés (position, direction et diamètre). Une extrémité libre peut changer de position. Aucun étirement automatique du réseau. Les actions impossibles sont grisées et expliquées au survol.

Les tests numériques couvrent les tés/coudes libres ou raccordés, les pièces asymétriques, les diamètres différents, l’ordre des connecteurs et un té orienté obliquement. La diminution du lag et les transactions sur les familles réelles restent à confirmer dans Revit.

## Correctif d’affichage après les premiers essais

**Affichage v4 :** le journal de la v3 montrait des dizaines de réinitialisations de la même sélection par seconde. La comparaison `_document != doc` comparait les wrappers .NET, alors que `Autodesk.Revit.DB.Document` redéfinit `Equals` mais pas les opérateurs `==`/`!=`. La comparaison utilise désormais l’égalité du document Revit, y compris dans `DocumentChanged`. Un test simule 100 wrappers distincts et équivalents : le délai reste inchangé et la vraie pastille apparaît au bout de 500 ms. Les changements réels de document, de vue et de sélection restent pris en compte.

**Affichage v3 :** la sélection et les données d’aperçu sont préparées ensemble dans un seul callback `Idling`. Le timer WPF, lié explicitement au dispatcher de la fenêtre, affiche directement la pastille après 0,5 s. Cette apparition ne dépend plus d’un second callback Revit ou d’un événement externe. La fenêtre utilise `Topmost` comme les rosaces des boutons et familles. Une indisponibilité temporaire suspend l’affichage sans perdre les données préparées. Le contrôle de lecture seule est réservé à l’application des rotations. Les fenêtres flottantes du même processus Revit sont reconnues. Les changements du document qui ne concernent pas les accessoires sélectionnés ne relancent plus le délai.

Le test de régression exerce la méthode réelle de présentation : aucune apparition à 499 ms, apparition à 500 ms avec un événement externe **null**, reprise après suspension et respect d’une fermeture volontaire. Le test ouvre les fenêtres hors écran ; il ne valide pas une session Revit ni les raccordements.

Le survol du bouton du ruban indique le dernier état : sélection détectée, sélection incompatible, vue non prise en charge, pastille affichée ou erreur. Le journal `%LOCALAPPDATA%\BIMaestro\Logs\mep-booster.log` conserve les transitions et les exceptions, ainsi que la version Revit et le chemin de la DLL chargée.

Les journaux des essais Revit 2025 montrent le chargement d’une DLL ciblant Revit 2023/.NET Framework, avec avertissements de versions d’assemblies. Ils prouvent l’exécution du service mais ne constituent pas une validation native Revit 2025/.NET 8. Le correctif d’affichage doit encore être vérifié en session Revit.

## Utilisation

1. Activer ON, puis sélectionner des accessoires de canalisation.
2. Attendre environ 0,5 seconde après la dernière sélection et le relâchement de Ctrl/Maj.
3. Survoler la petite pastille MEP pour ouvrir la rosace.
4. Survoler un angle : contour vert clair de la position future, axe jaune et flèche du mouvement.
5. Cliquer pour appliquer. Une seule opération Revit, annulable par Ctrl+Z, pour toute la sélection.

La rosace ferme après application, clic ailleurs, Échap, molette, navigation, modification du document ou changement de vue.
Elle ne revient pas après une fermeture volontaire avant une nouvelle sélection (ou OFF puis ON).
Un mouvement de souris seul ne la ferme pas. Ctrl/Maj suspend l’affichage et permet de compléter la sélection.

## Périmètre et limites

- Revit 2023 et 2024, vues en plan, coupes, élévations et 3D orthographiques. Pas de perspective ni de feuille.
- Accessoires et raccords droits, coudes à deux connecteurs, tés à trois connecteurs avec passage principal aligné. Les croix et raccords en Y sans passage aligné ne sont pas pris en charge.
- Éléments verrouillés, groupés ou imbriqués exclus. Une pastille « MEP · info » explique l’incompatibilité au survol.
- Rotations ±45°, ±90°, ±180° autour de l’axe propre à chaque accessoire.
- Le signe est défini dans le repère du projet : composante dominante de l’axe positive. Il ne prétend pas indiquer un sens hydraulique amont/aval. L’aperçu montre le mouvement dans la vue.
- « Inverser » est un retournement géométrique de 180° sur l’axe propre au raccord. Les pièces non hébergées et libres peuvent être retournées. Sur un réseau, les liaisons circulaires sont mémorisées, déconnectées puis rétablies sur les ports géométriquement compatibles. Un té symétrique peut échanger ses deux sorties principales en conservant son piquage ; un coude symétrique peut échanger ses deux extrémités. Les actions incompatibles sont exclues. Aucun paramètre hydraulique n’est modifié.
- Contrôle des positions des connecteurs et des connexions de la sélection et des voisins immédiats. Un changement de raccordement, un déplacement de connecteur ou un avertissement Revit annule toute l’opération.
- Aperçu sous forme de contours projetés superposés à la vue : pas d’occlusion par les murs, pas de simulation des contraintes. La validation effective reste celle de la transaction Revit.
- Au-delà de 20 pièces : axes et flèches pour chacune, sans contours.
- Une vue déjà en cours de manipulation ou une commande active peut retarder l’apparition : aucune modification de modèle ne s’exécute depuis le timer ou les événements souris.

## Recette dans Revit

Travailler sur une copie de maquette et comparer les connexions avant/après.

| Essai | Attendu |
|---|---|
| OFF, sélection d’une vanne | Aucune pastille |
| ON, sélection stable | Pastille décalée du pointeur après environ 0,5 s |
| Ctrl maintenu, ajout de plusieurs vannes | Aucune rosace pendant la sélection ; bon nombre après relâchement |
| Déplacement du pointeur vers la pastille puis un angle | Rosace accessible ; aperçu du bon angle, sans modification du document |
| ±45 et ±90, axes X/Y/Z, plan/coupe/3D | Rotation appliquée conforme au contour et à la flèche |
| Plusieurs accessoires orientés différemment | Chacun tourne sur son axe ; Ctrl+Z annule l’ensemble |
| Réseau raccordé | Aucun déplacement des ports voisins ni perte de connexion |
| Rotation interdite / élément non éditable | Annulation complète et message ; aucune modification partielle |
| Inversion non raccordée | Retournement bout pour bout conforme à l’aperçu |
| Inversion raccordée, deux extrémités identiques | Extrémités échangées, connexions rétablies et voisins immobiles ; sinon annulation complète |
| Deux pièces raccordées adjacentes sélectionnées | La liaison commune est reconnectée sur les ports opposés des deux pièces |
| Inversion hébergée / réduction raccordée | Bouton désactivé |
| Té avec piquage libre | Rotations du piquage autour du passage principal, sans déplacement des ports du passage |
| Té raccordé aux trois sorties | Rotations du piquage grisées ; inversion disponible si les trois points peuvent être conservés |
| Coude raccordé à une seule extrémité | Pivot sur cette extrémité, l’autre reste libre |
| Coude symétrique raccordé aux deux bouts | Inversion échange les deux ports ; rotations qui déplacent le second bout grisées |
| Échap, clic ailleurs, molette, panoramique, orbite | Disparition ; pas de réapparition spontanée |
| Changement de document, fermeture, autre application | Aucun aperçu résiduel |
| Bords de vue, deux écrans, échelles 100 % et 150 % | Rosace accessible, contour correctement aligné |
| Famille groupée/verrouillée ou raccord non pris en charge | Pastille d’information, pas de rotation |

## Vérification automatisée

`scripts/mep-booster-tests/MepBooster.UiTests.csproj` vérifie les commandes proposées, l’inversion conditionnelle, les événements de survol/clic et le placement des boutons. Il produit un PNG de la rosace, sans ouvrir Revit.

Les essais de raccordements, de navigation réelle et de correspondance aperçu/maquette nécessitent une session Revit ; les tests d’interface ne les remplacent pas.
