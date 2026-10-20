# Modes de préparation des suppressions — 19 octobre 2026

Les deux contrôles existants Léger / Détaillé gouvernent maintenant aussi les
recettes de restauration, et plus seulement le maillage d'aperçu. La préférence
existante est conservée ; le mode léger reste le défaut d'une nouvelle installation.

L'ouverture de « Qui a fait ça » ne parcourt plus la maquette : elle programme
le même inventaire différé que l'ouverture du document. Les anciens points
d'entrée PrimeDocument et PrimeFamilyParameterSnapshots délèguent également à
ce planificateur, qui conserve la file existante et ne recommence pas une
préparation terminée. Seuls les objets témoins de l'exercice pédagogique gardent
une capture ciblée immédiate avant leur suppression programmée.

| Léger : recettes directes prises en charge | Détaillé : compléments différés |
| --- | --- |
| Murs ordinaires non groupés | Escaliers, volées, paliers, rampes, garde-corps |
| Sols, toitures par contour et plafonds pris en charge | Groupes et leurs membres |
| Portes, fenêtres, familles à placement pris en charge | Équipements CVC, végétation |
| Tronçons de tuyaux, gaines, conduits et chemins de câbles | Terrains, topographies, imports, DirectShape |
| Courbes de modèle et paramètres | Systèmes MEP/électriques complets, connexions et relations géométriques |
| Aucun repli automatique vers une copie native | Objets sans recette directe nécessitant une archive native |

Les types déjà mesurés lents (>100 ms de capture de base) sont également réservés
à la préparation détaillée pendant la session. Un objet inconnu peut encore
provoquer une première pause. Les catégories ne garantissent ni la durée ni la
restaurabilité de toutes leurs variantes. Les équipements CVC sont classés de
façon conservatrice, y compris ceux dont certaines instances seraient rapides.

Le suivi des identités et paramètres reste actif pour les objets complexes en
léger. Leur boîte englobante et leur recette sont différées. Les captures simples
déjà présentes et les archives prêtes ne sont pas supprimées en changeant de mode.
Les compléments restent en file en léger et reprennent en détaillé. Les objets
nouveaux/modifiés sont également remis en file. Une suppression avant capture
reste possible et peut ne pas être restaurable.

Priorités : sélection, vue active, autres éléments. Les captures simples de
sélection continuent immédiatement par petits lots. Les objets complexes et les
copies natives attendent l'inactivité même lorsqu'ils sont sélectionnés. Les
terrains sont de nouveau admissibles en détaillé ; ceci remplace leur exclusion
automatique du 13 octobre. Le budget inactif reste 500 ms / 100 candidats avec
une seconde de pause, contrôlé entre éléments, sans interruption d'un appel API.
Les appels lourds restent sur le thread Revit et peuvent dépasser ce budget.

## Mesures du Mans

Journal : performance-2026-10-19-4dc0606c1f8344c391a302cb86bdf0c0.jsonl.
Maquette : Le Mans test plugin - Purger. Mesures de capture de base uniquement,
sans enrichissement ni archive native dans cet extrait.

| Catégorie | Échantillons | Moyenne ms | Maximum ms |
| --- | ---: | ---: | ---: |
| Murs | 217 | 1,38 | 38,07 |
| Sols | 30 | 2,43 | 7,21 |
| Fenêtres | 25 | 1,45 | 6,16 |
| Portes | 98 | 5,26 | 117,31 |
| Canalisations | 902 | 2,13 | 23,28 |
| Équipements CVC | 48 | 437,18 | 10800,95 |

Deux familles CVC prennent 10800,95 et 9600,85 ms. Le dernier lot d'inventaire
du journal laisse 8423 candidats sur 9795 initialement en file : il ne prouve
donc pas une couverture complète. Les moyennes ne sont pas des garanties.

## Vérifications

TestCaptureModes.ps1 exerce la classification de production sur des doubles de
métadonnées API : recettes ordinaires, classes complexes, CVC, mur groupé et type
mesuré lent. Il ne remplace pas un essai de restauration dans Revit.
