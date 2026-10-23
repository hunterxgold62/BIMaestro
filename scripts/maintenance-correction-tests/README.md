# Mémoire des corrections de maintenance

Lancer les tests autonomes depuis la racine du dépôt :

```powershell
dotnet run --project scripts/maintenance-correction-tests/MaintenanceCorrection.Tests.csproj --configuration Release
```

Le service et le format JSON de production sont compilés directement dans ce programme. Seuls les objets Revit et le contrôleur WPF sont remplacés : les tests ne lancent pas Revit. Le harness natif `MaintenanceGenericNativeChecks` vérifie séparément les familles réelles dans une copie de maquette.

La suite vérifie la trajectoire physique de la main et du centre du volume de dépose à cinq phases sur 60 transformations, y compris les miroirs et les inclinaisons. Elle couvre aussi les changements de géométrie, de paramètres, de raccordements et de référence produit, le renommage des types/familles, les hypothèses et la course de chaîne, l'absence de pieds/résultats réutilisés, la stabilité des avertissements sur 40 cycles, les corruptions JSON, les limites et la concurrence des écritures atomiques. Les fichiers d'essai sont isolés sous `tmp/maintenance-correction-tests/` ; le chemin précédent de la mémoire est rétabli en fin de programme.

## Contrat intégré

- `MaintenanceCorrectionMemory.Save(Document, FamilyInstance, MaintenanceWorkbenchState, out string)` enregistre après une action explicite de l'utilisateur. Il exige une commande et un profil valides ; la case de confirmation n'est pas une condition d'enregistrement.
- `TryApply` rétablit uniquement les repères et cotes du geste, sa notice et ses hypothèses. Les pieds, postures, collisions et confirmations antérieurs sont effacés. Le poste et l'analyse doivent être recalculés dans le projet courant.
- L’entrée de manœuvre générique peut reprendre un levier, un volant ou une chaîne mémorisés ; une recette de dépose d’actionneur reste réservée à la dépose explicitement choisie. Les deux interventions peuvent coexister sans se remplacer ni rendre l’autre ambiguë.
- `Remove` oublie le mécanisme courant pour la signature actuelle. Les autres mécanismes et variantes restent enregistrés.

Le fichier par défaut est `%LOCALAPPDATA%/BIMaestro/Maintenance/corrections-v1.json`. La variable `BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH` accepte un chemin absolu de fichier JSON, ou un dossier sans extension. Le test natif utilise un chemin isolé et n'accède pas à la mémoire réelle de l'utilisateur.

Le fichier et les recettes portent une version. La lecture est limitée à 2 Mio, 256 recettes et 16 niveaux JSON. Une mémoire corrompue ou d'une version inconnue est refusée et conservée ; elle n'empêche pas le fonctionnement ordinaire de l'outil. Les remplacements utilisent un fichier temporaire dans le même dossier, un vidage sur disque puis un remplacement atomique. Les écrivains utilisent un mutex par chemin ; deux reprises courtes restent possibles si un autre processus tient momentanément le fichier.

## Compatibilité et limites

La signature ne contient aucun identifiant Revit ni chemin de projet. Les noms libres de famille et de type restent des indications. L'accord exige les mêmes caractéristiques visibles locales, les mêmes raccordements et les mêmes paramètres géométriques/identifiants produit reconnus. Un type renommé peut correspondre ; une variante redimensionnée ou une autre référence produit ne peut pas hériter silencieusement du geste ou de la notice. Le dégagement de prise et la marge de collision font partie des réglages mémorisés ; la hauteur de prise liée au sol et les dimensions du mannequin restent propres au projet courant.

Les coordonnées géométriques sont quantifiées à 0,5 mm ; ce pas sert au rapprochement et n'applique aucune mise à l'échelle. Les transformations sont composées avant d'évaluer les petits points locaux, puis le bruit numérique est stabilisé à 0,000001 mm avant la quantification. Ce traitement évite qu'un point exactement à mi-pas change de case après un déplacement de plusieurs kilomètres ; la grille de rapprochement reste inchangée. Les paramètres numériques sont vérifiés plus finement. Les solides utilisent surfaces, contours et échantillons locaux. Pour les maillages, les diagonales internes aux faces coplanaires sont retirées avant le calcul de signature. Un changement de tessellation courbe ou de topologie peut entraîner un refus conservateur. Les géométries incomplètes, inconnues ou dépassant le budget de lecture sont refusées : aucune signature partielle n'est utilisée.

Une réflexion reconstruit un repère de maintenance droit et adapte simultanément l'angle initial, la course signée et le décalage local Y de dépose. Si Revit expose un `GetTransform` direct avec un miroir porté séparément par `FacingFlipped` ou `HandFlipped`, le service reconstruit d'abord ce repère physique. Une matrice déjà réfléchie ne reçoit pas une seconde réflexion ; des indicateurs miroir ambigus entraînent un refus. Une correction reste une donnée utilisateur réutilisée, jamais une certification ni une validation du nouveau poste.
