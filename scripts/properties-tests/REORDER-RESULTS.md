# Réordonnancement natif de la palette : preuve de faisabilité

Essai exécuté le 20 octobre 2026 dans **Revit 2025.5**, sur un projet jetable. [Voir les captures comparées](ReorderGallery.html).

## Résultat confirmé

Le bloc **Étendues**, avec son en-tête, ses huit propriétés et sa ligne de séparation, a réellement été déplacé devant **Niveau en fond de plan** dans la grille native. Aucune zone de favoris n'a été ajoutée, aucune ligne dupliquée et aucun déplacement de pixels utilisé.

Le contrôle contient 47 lignes plus la ligne d'en-tête 0. Avant : Niveau en fond de plan 20–24, Étendues 25–34. Après : Étendues 20–29, Niveau en fond de plan 30–34. `CGXGridCore::MoveRows` a retourné 1, sans installer le composant facultatif ImplementMoveRowsCols. Le test compare toutes les colonnes de contenu 1–3 à la permutation attendue : **True**. La colonne 0 contient des numéros de lignes générés automatiquement ; elle est exclue de cette comparaison car ces numéros changent lors du déplacement.

Empreinte des paramètres de la vue et du mur identique avant/après : `40DA2F888AD9A43E1D317B5B98A7EAE0C4C2D3B45ADDA5A524FD78E67CC5FA02`. `Document.IsModified=False` à la fin du déplacement. Aucun paramètre du modèle n'a été édité pour ce test.

## Interactions et limites

- Le clic de repli du groupe déplacé a masqué ses huit propriétés, en conservant le groupe Niveau en fond de plan visible en dessous : capture `collapse-test.jpg`.
- Une interaction ultérieure a été suivie d'un retour visuel à l'ordre initial. La cause exacte et les événements de reconstruction ne sont pas isolés ; le prototype n'impose pas l'ordre après reconstruction.
- Le redéploiement, les éditeurs de valeurs, listes, cases et boutons ne sont **pas qualifiés de bout en bout après déplacement**. Aucun test d'écriture de paramètre n'est revendiqué.
- Les autres catégories, les murs, familles, sélections multiples, thèmes et versions 2023/2024/2026 ne sont pas validés pour ce réordonnancement.
- La fonction inverse `restore` est présente dans le code mais son exécution n'est pas revendiquée comme validée. Le retour visuel à l'ordre initial vient ici d'une reconstruction native. Le manifeste et les commandes temporaires ont été retirés. Les sessions contenant d'autres documents de l'utilisateur ont été laissées ouvertes.

C'est une preuve de faisabilité de déplacement natif, pas une fonction de personnalisation prête à distribuer.

## Accès à la grille

1. Symboles **publics Microsoft**, correspondant exactement au CodeView RSDS de mfc140u.dll : `mfc140u.amd64.pdb`, clé `F72869642050B9BF900FE69E3BEF03A01`.
2. Résolution de `CWnd::FromHandlePermanent` : RVA `0x2957c0`. Le code exige le SHA256 du binaire chargé `FC9B1F94752D7080436824B480E83BA1EC930E3F7BAA881F4E842B882E715CFC` avant de l'utiliser.
3. Identification du véritable objet `ElemGrid`, dérivé d'`AGrid`, puis `CGXGridWnd`, dans DesktopMFC.dll.
4. Lecture du RTTI x64, vérification des régions mémoire, du module et de l'unicité de la base non virtuelle CGXGridCore. Le décalage 232 de cet essai est **lu dans les métadonnées du type à l'exécution**, pas supposé dans le code.
5. Lecture des vraies cellules via les exports Objective Grid. Repérage des groupes par leur libellé et du prochain en-tête par sa cellule de groupe.
6. Appel de `MoveRows` sur l'instance vérifiée, puis relecture complète des contenus et contrôle de l'empreinte métier. Les paramètres utilisés pour l'appel exploratoire sont flags=0 avec redessin explicite et commandType=0 ; aucun contrat de compatibilité interversion n'en est déduit.

Ces bibliothèques internes ne constituent pas une API publique Revit. Aucun patch de binaire, scan de mémoire arbitraire, pointeur C++ inventé ou changement des définitions de paramètres de famille n'a été utilisé.

## Reproduction du laboratoire

Source et build : `native-reorder/Experiment.cs`, `native-reorder/Build.ps1`. Le harnais commun doit être chargé dans une instance jetable. Le prototype exige Revit 2025 et le chemin exact `PropertiesFixture.rvt` à côté du harnais.

Créer `target-pid.txt` avec le PID de cette instance. `experiment.txt` accepte `inspect`, `top`, `move`, `move-enabled` ou `restore`, et `control.txt` contient `run CHEMIN_DLL`. `move-enabled` et `restore` ne font pas partie des actions qualifiées ici. Le harnais de la session a été observé dans plusieurs processus : une garde de PID et un nombre borné de réessais ont été nécessaires. Un vrai outil devra fournir une commande par processus plutôt que réutiliser cette file commune.

Le dossier `evidence/reorder/` conserve la lecture initiale, le premier essai, la permutation vérifiée et les captures natives. Le premier test de comparaison incluait les numéros automatiques de la colonne 0 et retournait False ; la comparaison finale des contenus exclut cette colonne et retourne True. Les deux rapports sont conservés.

## Sources primaires

- [Perforce : MoveRows, déplacement des véritables lignes](https://help.perforce.com/stingray/2024.1/Stingray_Studio_API_Documentation/Content/Grid/CGXGridCore_8a0j.htm)
- [Perforce : héritage de CGXGridWnd](https://help.perforce.com/stingray/2024.1/Stingray_Studio_API_Documentation/Content/Grid/CGXGridWnd_5jvo.htm)
- [Microsoft : état des modules MFC](https://learn.microsoft.com/en-us/cpp/mfc/tn058-mfc-module-state-implementation?view=msvc-170)
- [Symboles Microsoft correspondant au binaire testé](https://msdl.microsoft.com/download/symbols/mfc140u.amd64.pdb/F72869642050B9BF900FE69E3BEF03A01/mfc140u.amd64.pdb)
