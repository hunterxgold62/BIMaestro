# Aperçus historiques reconstruits

Tous les modes conservent une recette versionnée pour les instances ponctuelles
sur un niveau (libres ou hébergées), les murs de base verticaux sans profil modifié,
et les sols plats définis par des lignes/arcs, avec leurs boucles intérieures.
Les identités des types, niveaux, hôtes et références de paramètres sont des UniqueId.
Les unités numériques sont les unités internes Revit.

La recette d'un mur/sol hôte peut aussi être conservée lorsqu'il est joint ou découpé,
avec un maillage séparé pour l'aperçu détaillé. Les formes non prises en charge
(familles en place, familles adaptatives/sur face/sur deux niveaux, murs
inclinés/attachés/à profil modifié, sols modifiés par points) restent sur l'ancien aperçu.
La recette utilise le type dans son état actuel et ne sauvegarde pas sa définition.

Une sous-transaction crée brièvement l'élément, copie les coordonnées des triangles
et annule toutes les mutations avant de créer le DirectShape d'aperçu. Le nettoyage
des aperçus reste identique. Si l'original a été rétabli et existe encore, son UniqueId
sert à le sélectionner directement.

## Restauration native définitive

**Restaurer les éléments** traite la ligne/le cluster sélectionné, ou les lignes
sélectionnées dans le tableau. **Restaurer sélection** traite uniquement les éléments
sélectionnés dans le détail du cluster. Les familles, murs et sols sont recréés comme
de vrais objets Revit, conservés après enregistrement/réouverture, avec de nouveaux
identifiants Revit. Les hôtes sont restaurés avant leurs instances.

Les tuyaux, gaines, conduits électriques et chemins de câbles rectilignes conservent
leurs extrémités en coordonnées absolues (y compris pentes et verticales), type,
niveau, section et type de système. Les flexibles circulaires de tuyauterie/gaine conservent
leurs points et tangentes. Les accessoires/familles admissibles conservent aussi
leur orientation 3D. Les connexions physiques enregistrées sont rétablies dans une
seconde passe, après création de tout le lot, vers les voisins existants ou restaurés.
Les deux connecteurs doivent correspondre à leurs positions et directions historiques,
être libres et coïncider. Un voisin déplacé ou déjà connecté ailleurs n'est pas modifié.
Les échecs de connexion sont signalés sans supprimer les éléments recréés. On peut
restaurer le voisin manquant ultérieurement ; ses références retrouvent les éléments
déjà restaurés via leurs marqueurs d'origine. Aucun raccord de remplacement n'est
inventé lorsqu'une connexion directe ne peut pas être rétablie.

À la demande de l'utilisateur, le calorifuge est exclu de la restauration et des
comptages d'erreurs, y compris pour les anciens historiques. Les catégories natives
d'isolants et les libellés contenant « calorifuge » sont filtrés. Les tuyaux,
raccords et accessoires restent traités et leurs erreurs restent visibles.
Les familles ponctuelles admissibles en miroir conservent désormais leur repère
réfléchi ; une rotation seule ne peut pas reproduire une symétrie.

Cela ne constitue pas une sauvegarde intégrale du réseau : pièces de fabrication,
circuits électriques logiques, familles sur face,
jonctions et relations non enregistrées ne sont pas reconstitués. Le type de système
est conservé, mais Revit recalcule les systèmes à partir des connexions ; leur ancien
identifiant/nom n'est pas garanti. Les types/familles doivent rester dans le projet.

Un marqueur Extensible Storage persistant associe les éléments restaurés à leurs
origines : un second clic ne crée pas de doublon. Une nouvelle suppression permet
une nouvelle restauration. Les marqueurs suivent l'annulation des transactions.
Les opérations réussies forment une seule action d'annulation Revit ; les erreurs
par élément sont annulées séparément et présentées dans le bilan.

Pour une restauration définitive, aucun maillage ne remplace un élément natif.
Les événements antérieurs sans recette, les formes non prises en charge et les
types/niveaux/hôtes manquants sont explicitement signalés comme non restaurés.
La capture de la recette ne dépend plus du choix Simple/Détaillé.
Le préchargement en arrière-plan fonctionne aussi en mode Simple. Les limites de
temps/nombre des aperçus ne tronquent plus les recettes d'une grande sélection ni
les suppressions en lot ; les éléments ajoutés/modifiés hors budget détaillé ont
une capture simple de secours. L'ouverture doit néanmoins laisser au préchargement
le temps de parcourir la maquette : un élément supprimé par un autre outil avant
toute capture ne peut pas être reconstitué rétroactivement.

Un refus/échec de capture est maintenant sauvegardé dans `captureFailure` avec la
classe réelle et, pour les familles, le mode de placement/miroir/sous-composant.
Une erreur de lecture d'une connexion ne supprime plus la recette géométrique du
tronçon : les liens incomplets sont enregistrés comme avertissements de capture.
Le bilan écrit automatiquement un rapport JSON exhaustif dans
`%LOCALAPPDATA%/BIMaestro/HistoryReports` et affiche son chemin. Ce rapport contient
tous les résultats par élément et toutes les erreurs de connexion (sans limite de
de lignes), ainsi que leurs catégories/identifiants. Une erreur d'écriture du
rapport ne remet pas en cause une restauration déjà validée.

## Banc natif

Depuis la racine du dépôt :

```powershell
./scripts/history-tests/Build.ps1 -RevitVersion 2024 -RunName history-validation
./scripts/codex-tests/StartNativeHarness.ps1 -RevitVersion 2024 -RunName history-validation
```

Le lanceur ouvre un processus Revit distinct. Le banc refuse tout processus contenant
un document ouvert et ne travaille que dans une nouvelle maquette non enregistrée.
Il nécessite les gabarits de familles anglais installés. Résultats dans
`tmp/codex-native-validation/2024-history-validation/result.json` ou `error.txt`.
Utiliser un nouveau RunName pour relancer.

Cas : mur droit avec décalage et ligne de justification, mur courbe, sol troué,
famille tournée avec dimension d'instance, famille hébergée sur mur, références
manquantes, recette invalide, recherche de l'original. Les tests vérifient le passage
JSON, la suppression réelle avant reconstruction, les dimensions et volumes,
l'absence d'éléments natifs résiduels et la validité du DirectShape après annulation.
Le banc vérifie aussi une vraie restauration après sauvegarde/réouverture, les
paramètres d'instance, la restauration conjointe hôte/famille, l'absence de doublons
après réouverture, la nouvelle restauration après suppression, l'annulation du
groupe de transactions et un lot mêlant succès, type absent et données invalides.

Validation du 19 septembre 2026 : 25 contrôles natifs Revit 2024 réussis,
rapport `tmp/codex-native-validation/2024-history-network-diagnostics-v4/result.json`.
Aux 15 contrôles initiaux s'ajoutent : les quatre catégories de tronçons rigides,
les deux catégories de flexibles circulaires, les connexions à un voisin existant,
leur persistance après réouverture, la restauration en plusieurs lots, l'accessoire
tourné connecté à deux tuyaux, le refus d'une inclinaison interdite sans boîte
modale et le respect d'un accessoire déplacé depuis l'enregistrement.
Deux contrôles supplémentaires vérifient la famille tournée en miroir (état
`Mirrored`, repère et boîte englobante) et l'exclusion du calorifuge des comptages,
sans perdre les erreurs/diagnostics des autres familles. Le booléen Revit `Mirrored`
est conservé séparément : le déterminant de `GetTransform()` ne suffit pas pour
identifier une famille en miroir.

Le banc crée une famille d'accessoire synthétique qui interdit son inclinaison :
ce refus est un test explicite de rollback, pas une invitation à supprimer une
occurrence dans une fenêtre Revit. Ses transactions de préparation possèdent aussi
un traitement des erreurs pour ne pas ouvrir de boîte bloquante.

### Raccords CML et Revit 2025

`Build.ps1 -RevitVersion 2025` utilise le banc .NET 8 `HistoryNative2025.csproj`.
Si son dossier de sortie contient `CML-source.rvt`, le banc ouvre cette **copie**
isolée (détachée si collaborative), jamais le modèle utilisateur d'origine.
Il ferme sans enregistrer et annule chaque scénario. Sinon il exécute les tests
synthétiques habituels dans une maquette vierge.

Les scénarios CML couvrent dix couples famille/dimensions/angle, avec les nouvelles
recettes, les anciennes sans identifiants/angles de connecteurs et la réparation
d'un raccord déjà restauré. Ils contrôlent les connexions, l'absence de tuyaux
temporaires résiduels et l'idempotence. La suppression du calorifuge par Revit au
moment de supprimer le raccord de test est consignée séparément.

La reconstruction conserve les diamètres et angles des connecteurs même si les
paramètres d'origine étaient en lecture seule. Pour les réductions pilotées par
le solveur, des tronçons temporaires servent au calcul natif, puis sont supprimés.
Le type et tous les connecteurs doivent correspondre exactement à l'historique.
La réparation des occurrences déjà restaurées est annoncée dans la confirmation ;
elle protège les originaux, les déplacements, les nouveaux voisins et les
dépendances que le remplacement risquerait de supprimer.

Validation : `2025-history-cml-legacy-final/result.json` — 30 scénarios réussis,
60 connexions rétablies, 10 réparations d'éléments déjà présents, aucun échec.
Les variantes anciennes omettent également les paramètres numériques de famille
afin de tester la récupération à partir des seules extrémités historiques.
Régression synthétique : `2024-history-connectors-regression-v1/result.json` —
25 contrôles réussis. Compilations complètes Debug Revit 2024 et 2025 réussies.

### Derniers défauts visibles : piquages et réductions de gaines

Un fichier `CML-targets.json` dans le dossier de sortie peut sélectionner les noms
de familles à tester. Pour le cas ciblé, le banc exige la présence des six IDs du
rapport : 903986, 903766, 877948, 770610, 770608 et 744493. Les anciennes recettes
omettent les dimensions en lecture seule, mais conservent les paramètres qui
étaient modifiables (notamment la longueur personnalisée). Chaque restauration
vérifie aussi les extrémités des tuyaux/gaines voisins, pas seulement leurs IDs.

Les piquages utilisent `NewTakeoffFitting` sur la canalisation historique existante
ou déjà restaurée. Un hôte absent est signalé ; aucun voisin n'est choisi par
proximité. Les préférences de routage de calcul sont isolées dans des copies de
types temporaires, supprimées avant validation ; le type de la canalisation
porteuse est rétabli. Les réductions sont créées avec leur famille exacte ; les
deux ordres d'extrémités peuvent être essayés pour respecter leurs paramètres de
décalage. Le recalage d'un raccord déconnecté est autorisé seulement si une même
translation fait coïncider tous ses connecteurs historiques, dimensions et
directions comprises. Les tolérances de reconnexion ne sont pas élargies.

Validation du correctif :

- `2025-history-visible-v5/result.json` : 24 scénarios réussis (les six occurrences
  exactes et deux piquages supplémentaires, chacun en capture récente, ancienne
  et réparation d'une occurrence déjà recréée).
- `2025-history-routing-regression/result.json` : 30 scénarios acier réussis.
- Compilations complètes Debug Revit 2024 et Revit 2025 réussies.

Le calorifuge reste exclu. Les quatre systèmes logiques en échec du rapport ne
sont pas masqués ni traités par ce correctif ciblant les six objets visibles.
