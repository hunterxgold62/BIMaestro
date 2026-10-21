# Tests natifs isolés de l'ordre des propriétés

`Build.ps1 -RevitVersion 2025` compile le vrai code de production de la grille, de l'ordre, des préférences, du contexte et du service dans un complément de laboratoire. `BuildAll.ps1` détecte les installations 2023–2027 et compile chacune avec ses propres API. La compilation ne constitue pas une validation native.

Sortie : `tmp/properties-order-tests/ANNEE`. Charger le manifeste temporaire produit dans une nouvelle instance de laboratoire. Les commandes ne sont consommées que lorsque `target-pid.txt` contient exactement le PID courant et que la version du processus correspond à `version.txt`. Ne pas partager ce dossier entre deux instances de la même année.

Toutes les commandes après `fixture` refusent un document différent du chemin exact `PropertiesOrderFixture.rvt`. La création de la fixture est la seule écriture API de paramètres : elle crée un mur et initialise ses commentaires à `ORDER_BASELINE`. Les tests de saisie suivants doivent utiliser le véritable éditeur Revit, jamais Parameter.Set.

Écrire une commande dans `control.txt`, attendre le rapport correspondant avant la suivante :

1. `fixture` crée et ouvre le document jetable propre à l'année.
2. `select-wall` sélectionne son mur ; attendre le rafraîchissement natif.
3. `baseline` enregistre tous les paramètres de toutes les instances et tous les types du document.
4. `raw-move` déplace directement les commentaires en premier dans leur groupe et ce groupe en tête, pour observer le mécanisme natif avant correction. Cette commande contourne uniquement le verrou du layout dans le harnais ; elle ne modifie pas le produit.
5. `move` (ou `configure`) utilise les vraies préférences isolées et le vrai service pour appliquer le même ordre avec la correction de production.
6. `report` ou `inspect` fournit `report.json` : version/build/PID, état du service, libellés, valeurs, marqueurs et rectangles client de toutes les lignes natives, empreinte et valeurs complètes des paramètres. Le HWND permet de convertir les rectangles dans le repère écran.
7. Saisir une valeur unique dans le champ Commentaires natif ; valider avec Entrée puis sortir de l'éditeur. Écrire cette valeur dans `expected-value.txt`, puis envoyer `verify-write`. Le test réussit seulement si le commentaire attendu est écrit et si aucun autre paramètre du document n'a changé. Le résultat est dans `verify-write.json` ; un échec produit aussi `error.json`.
8. `restore` retire les préférences isolées via le vrai service. Après un `raw-move`, changer de sélection (`select-view`, puis `select-wall`) reconstruit la grille native ; le déplacement brut ne possède pas l'état de restauration du service.

`verify-unchanged` compare intégralement le document au dernier `baseline.json` et exige zéro différence. L'utiliser après un déplacement sans saisie, une saisie annulée ou une restauration.

Pour tester un autre éditeur, créer `expected-write.json` avant `move`/`raw-move` :

```json
{"Parameter":"WALL_BASE_OFFSET","Value":"1","Tolerance":0.000000001,"AllowedDependentParameters":["HOST_AREA_COMPUTED","HOST_VOLUME_COMPUTED"]}
```

`Parameter` est le nom exact du BuiltInParameter du mur. `Value` est la valeur brute API attendue : texte exact, double en unités internes Revit, entier `0`/`1` pour un booléen, identifiant numérique pour un ElementId. Le harnais n'écrit jamais cette valeur : la saisir dans Revit. La tolérance ne s'applique qu'aux doubles. Les paramètres dépendants autorisés doivent être listés explicitement par nom BuiltInParameter ; ils ne sont permis que pour une cible double et le même mur, et leurs changements restent détaillés dans les preuves. Sans ce fichier, la cible reste Commentaires et la valeur provient de `expected-value.txt`.

`show-properties` poste l'ouverture de la palette native si elle est masquée. `shutdown-fixture` poste Fermer via l'interface Revit ; choisir **Ne pas enregistrer** dans le dialogue natif. Il poste ensuite Quitter Revit uniquement lorsque aucun document ne reste ouvert et lorsque la fixture était le seul document au moment de la demande. Il ne ferme pas les documents de l'utilisateur.

Pour recompiler pendant qu'un ancien harnais est chargé, utiliser `-OutputTag '-r2'` (également accepté par BuildAll). Charger le nouveau manifeste dans une nouvelle instance ; le CLR ne remplace pas une DLL déjà chargée. Chaque sortie possède une fixture et une file de commandes distinctes.

La fixture actuelle crée deux murs et un niveau supplémentaire `ORDER_LEVEL_2`. `select-wall2` choisit le second mur, `select-multiple` les deux. Les vérifications d'écriture ciblent le mur sélectionné (sinon le premier). Les anciens fichiers fixture ne sont pas modifiés automatiquement : utiliser un nouveau OutputTag pour obtenir cette géométrie.

Chaque rapport contient `mappings` pour tous les paramètres du contexte de production : identifiant natif, libellé attendu, ligne retournée par `FindParameterRow`, libellé réel, concordance et erreur éventuelle. Une ligne absente (`-1`) est explicitement signalée. `raw-move` conserve automatiquement `report-before-raw-move.json` puis `report-raw-move.json` ; envoyer `report` après un clic pour observer la correspondance après interaction.

`native-state` produit un rapport avec le focus (HWND, classe, texte), l'état `IsActiveCurrentCell`, la cellule courante via `GetCurrentCell`, l'état de saisie de production et les fenêtres enfant de la grille. Les erreurs sont conservées dans le rapport. Ce diagnostic peut être envoyé pendant une saisie en attente pour distinguer le texte de l'éditeur et la valeur réellement validée dans le modèle. Le harnais appelle `SetRaiseWithoutDelay` pour consulter sa file même sans nouveau mouvement de souris ; cette boucle est réservée au laboratoire et ne fait pas partie du service livré.

Après le premier rapport, un `DispatcherTimer` de laboratoire écrit `timer-state.json` toutes les secondes, même lorsqu'un éditeur bloque Idling. Il lit seulement la dernière grille native capturée en contexte autorisé et son état/focus/fenêtres enfant ; aucun accès à l'API Revit DB/UI n'est fait depuis le timer. Les fenêtres de focus, grille et éditeurs incluent leurs rectangles écran `GetWindowRect`, permettant de cibler le véritable GXEDIT. Le timer s'arrête à la fermeture du complément.

`python scripts/properties-order-tests/CollectResults.py` recalcule une matrice depuis les paires `text/bool/double/list-before.json` et `*-after.json`, sans modifier les preuves. Il compare tous les paramètres, exige la valeur cible attendue et les seules dépendances explicitement autorisées, puis vérifie que l'ordre était réellement actif et que la cible était première dans le premier groupe avant saisie. Une désélection après validation peut remettre `hasApplied` à false et n'annule pas cette preuve préalable. Les correspondances natives visibles, PID, version, build, document et chronologie sont contrôlés. La présence d'une DLL et le démarrage restent séparés de toute qualification native. `--write` actualise explicitement le rapport compact `evidence/results.json` ; il conserve les deltas et SHA256 des sources, sans copier les snapshots de plusieurs mégaoctets.

`report-COMMANDE.json` conserve le dernier rapport de chaque commande. Copier les rapports à chaque scénario pour conserver l'historique. Ne pas confondre les rapports historiques avec l'état courant ; leurs dates et PID sont explicites. Les préférences réelles de l'utilisateur ne sont jamais lues ni réinitialisées : le stockage est redirigé vers `isolated-preferences.json`.

Matrice minimale à exécuter dans chacune des cinq versions : ordre de groupe et de paramètre réel ; saisie texte puis autre champ voisin ; Échap et Entrée ; Tab/Maj+Tab ; défilement ; repli/dépli ; changement mur/vue/mur ; reconstruction après transaction ; désactivation/restauration. Compléter les snapshots par les valeurs attendues du champ testé, les rectangles et des captures de l'interface. Une empreinte inchangée avant saisie et une seule différence intentionnelle après saisie sont nécessaires, mais ne suffisent pas à démontrer tous les éditeurs (listes, booléens, unités, boutons de dialogue et champs en lecture seule).

## Préparation de l'installation de production

`InstallBuilds.ps1` prépare uniquement un aperçu par défaut. `-Preview` est équivalent ; `-RevitVersions 2024,2025` limite les versions. Aucune installation ne se produit sans `-Install`. Ne lancer cette dernière option qu'après l'autorisation d'installation ; elle écrit dans le dossier BIMaestro local et dans les manifestes Revit de l'utilisateur. L'exécution depuis un profil sandbox éphémère est refusée pour éviter de référencer des DLL temporaires. Un aperçu produit dans ce profil affiche sa racine redirigée ; refaire l'aperçu sur l'hôte réel avant installation.

Le script exige les résultats de compilation finaux, les empreintes des sources et les empreintes des DLL dans `tmp/properties-order-tests/production-build-final-*.json`. Si les sources ou les builds changent, il faut recompiler puis actualiser ces preuves ; le script refuse un artefact différent. Il vérifie les types de production, les références API propres à l'année et le runtime réellement installé par lecture des métadonnées PE, sans charger les DLL dans Revit ou PowerShell.

Chaque package contient les fichiers de sortie et dépendances, hors PDB. Son dossier porte l'année et le SHA256 de l'inventaire complet. Les fichiers sont copiés dans un nouveau dossier de staging, vérifiés, puis ce dossier est renommé. Un package existant est vérifié et réutilisé sans aucune écriture ; un contenu différent est refusé. Aucun ancien dossier ni DLL chargée n'est remplacé ou supprimé. Les liens symboliques et jonctions dans les packages sont refusés ; les fichiers cloud OneDrive ordinaires sont acceptés.

Tous les packages sont préparés avant de modifier le premier manifeste. Chaque `BIMaestro.addin` est sauvegardé puis remplacé atomiquement, en préservant ses autres entrées. Une erreur durant le changement des manifestes provoque un retour aux octets réellement sauvegardés, uniquement si aucun autre processus n'a modifié le manifeste installé entre-temps. Les sauvegardes restent disponibles. Les anciens Revit ouverts conservent leur DLL ; le changement prend effet au prochain démarrage. Un échec de staging laisse le dossier temporaire pour inspection, sans suppression automatique.

L'installation ne constitue pas une qualification native supplémentaire et ne modifie pas les catégories/versions autorisées dans le code compilé. Le script écrit un reçu `tmp/properties-order-tests/installed-production.json` seulement après une installation réussie.
