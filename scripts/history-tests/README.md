# Aperçus historiques reconstruits

Tous les modes conservent une recette versionnée pour les instances ponctuelles
sur un niveau (libres ou hébergées), les murs de base verticaux sans profil modifié,
et les sols plats définis par des lignes/arcs, avec leurs boucles intérieures.
Les identités des types, niveaux, hôtes et références de paramètres sont des UniqueId.
Les unités numériques sont les unités internes Revit.

La recette d'un mur/sol hôte peut aussi être conservée lorsqu'il est joint ou découpé,
avec un maillage séparé pour l'aperçu détaillé. Les formes non prises en charge
(familles en place, miroirs, familles adaptatives/sur face/sur deux niveaux, murs
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

Cela ne constitue pas une sauvegarde intégrale du réseau : isolants/revêtements,
pièces de fabrication, circuits électriques logiques, familles sur face/en miroir,
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

Validation du 19 septembre 2026 : 23 contrôles natifs Revit 2024 réussis,
rapport `tmp/codex-native-validation/2024-history-network-v8/result.json`.
Aux 15 contrôles initiaux s'ajoutent : les quatre catégories de tronçons rigides,
les deux catégories de flexibles circulaires, les connexions à un voisin existant,
leur persistance après réouverture, la restauration en plusieurs lots, l'accessoire
tourné connecté à deux tuyaux, le refus d'une inclinaison interdite sans boîte
modale et le respect d'un accessoire déplacé depuis l'enregistrement.

Le banc crée une famille d'accessoire synthétique qui interdit son inclinaison :
ce refus est un test explicite de rollback, pas une invitation à supprimer une
occurrence dans une fenêtre Revit. Ses transactions de préparation possèdent aussi
un traitement des erreurs pour ne pas ouvrir de boîte bloquante.
