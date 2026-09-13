# Flux MEP : état des lieux et trajectoire proposée

Date : 12 septembre 2026. Document de cadrage, pas une validation hydraulique.

**Suivi :** les constats ci-dessous décrivent l'état initial avant corrections. L'implémentation et les vérifications des jalons 1 et 2 sont consignées dans [MEP-jalons-1-2-livraison.md](MEP-jalons-1-2-livraison.md).

## Décision proposée

Faire de Maquette MEP et du viewer web deux interfaces d'un même modèle de réseau, avec les mêmes scénarios et les mêmes résultats. La première promesse doit être : comprendre les connexions, expliquer les sens supposés et identifier les conséquences d'une coupure dans la maquette. Le calcul de débit et de pression viendra ensuite, sur un domaine explicitement pris en charge.

Le prochain jalon recommandé est **« diagnostic et scénarios cohérents Revit/web »**. Il apporte une utilité métier immédiate et prépare le calcul physique.

## Périmètre et vérifications

- Plugin examiné au commit `9451e4de5aa0a9fcd010f2cd06167b1b197fdb97`.
- Sources du viewer récupérées et examinées au commit `f4f920e26125b08e8863f7d65dec9d30849b61b3`, correspondant à la version Sites 33. Site : https://viewer.bimaestro.fr.
- Compilation et exécution du programme de régression MEP : **442 assertions réussies**, plus les contrôles d'export annoncés par ce programme.
- Exécution directe du moteur TypeScript sur un petit réseau source → tuyau → vanne → tuyau : divergences de circulation et d'état de vanne reproduites.
- La suite complète du viewer n'a pas été relancée. Pas d'essai sur une maquette de production ni de comparaison exhaustive entre les moteurs.
- Aucun changement fonctionnel ni déploiement réalisé dans le cadre de ce point. Les propositions ci-dessous restent à implémenter.

## Ce que font les flux aujourd'hui

Le plugin extrait un graphe de connecteurs et de connexions Revit, détecte des équipements et des vannes, et applique les réglages du scénario. Il détermine l'accessibilité des portions de réseau, puis leur sens supposé. Il utilise notamment les arrivées/retours, les ports In/Out, des contraintes de pompe, des pondérations de jonctions par section et des règles de continuité.

Un potentiel numérique entre 0 et 1 aide à orienter les chemins. Ce potentiel ne représente pas une pression en Pa ou en bar. Les pondérations ne constituent pas un calcul complet des pertes de charge ; le modèle de résultat n'expose pas un débit hydraulique calculé. La vitesse des particules est une constante de rendu.

Le plugin distingue déjà trois dimensions utiles : `FlowState`, `HasCirculation` et `DirectionState`, avec une explication et un niveau de fiabilité du sens. Les scénarios, l'historique, les diagnostics et le rejeu sont également de bonnes bases.

L'export transmet le graphe et les résultats du plugin dans `mep.json`. Le site conserve ces résultats tant que le scénario exporté est inchangé. Après modification, il recalcule l'accessibilité avec un parcours par distance depuis les arrivées et conserve les directions déjà résolues par Revit. Il ne reproduit donc pas le moteur C# complet.

## Écarts constatés et priorités

| Priorité | Constat | Conséquence | Correction proposée |
|---|---|---|---|
| P1 | Deux algorithmes différents après modification du scénario web | Une même fermeture peut donner une autre interprétation dans le plugin et le site | Définir des règles communes et exécuter les mêmes cas de référence dans les deux environnements |
| P1 | Le web assimile des distances différentes depuis une arrivée à une circulation | Une branche en impasse peut rester animée | Calculer séparément accessibilité et existence d'un chemin de circulation admissible |
| P1 | Le web ne recalcule pas `upstreamState` / `downstreamState` des vannes | La fiche peut contredire la couleur du réseau après une action | Produire tous les résultats et explications à partir de la même révision de scénario |
| P1 | Le web conserve des sens résolus et leurs explications après changement de contexte | Un résultat historique peut apparaître encore applicable | Invalider les résultats dépendants du scénario ; distinguer une contrainte manuelle d'un ancien résultat automatique |
| P1 | Le plugin anime seulement les chemins résolus ; le web vérifie alimentation et circulation sans imposer cette condition | Un sens inconnu peut être animé sur le site | Même règle d'affichage des directions inconnues ou conflictuelles |
| P1 | Un retour seul rend des zones `Supplied` dans le plugin ; le moteur web ne les considère pas alimentées | Le mot « alimenté » a deux significations | Distinguer « relié à une arrivée » et « relié à un retour » |
| P2 | « Sous pression » et « gradient hydraulique » sont utilisés pour des résultats indicatifs | Le vocabulaire donne plus de certitude que le calcul | Employer « relié à l'arrivée », « circulation non établie » et « sens déduit » |
| P2 | Les extrémités topologiques libres peuvent devenir des sorties implicites dans le plugin | Une coupure de modélisation peut être interprétée comme un débouché | Qualifier les extrémités : terminal, retour, limite d'export, bouchon, connecteur à vérifier |
| P2 | Les organes détectés comme vannes sont tous modélisés en isolement | Une soupape ou un composant multivoie peut avoir un comportement trop simplifié | Qualifier le rôle et les passages internes de chaque type d'organe |
| P2 | Certaines règles stabilisent les directions en conservant un résultat antérieur lors de l'ouverture de vannes | L'historique peut participer au résultat | Vérifier la reproductibilité d'un état final identique après des séquences différentes |

P1 : indispensable pour promettre des scénarios cohérents. P2 : clarification ou extension nécessaire pour renforcer la fiabilité métier.

### Reproduction concrète

Réseau simple : arrivée → tuyau amont → vanne → tuyau aval. Fermer la vanne dans `applyScenario`.

- Le tuyau aval devient isolé : comportement attendu.
- Le tuyau amont conserve `hasCirculation: true`, avec `directionState: 0`. Les conditions d'affichage du web permettent son animation.
- La vanne conserve `downstreamState: 2` alors que le tuyau aval est à `flowState: 1`.
- En remplaçant la seule arrivée par un retour, le web renvoie quatre éléments inconnus. Le plugin dispose au contraire d'un traitement explicite des réseaux avec retour seul, couvert par un test existant.

Le premier problème vient du fait qu'être atteignable depuis une arrivée ne prouve pas qu'un fluide circule. La correction doit concerner le calcul et l'affichage ensemble.

## Principe commun à adopter

### Quatre couches distinctes

1. **Maquette observée** : identités, connexions, systèmes, géométrie et valeurs Revit, avec leur provenance. Une connexion absente reste une donnée manquante à expliquer.
2. **Hypothèses du scénario** : arrivées, retours, terminaux, vannes, équipements actifs, corrections manuelles et limites du modèle. Une correction reste identifiable comme une hypothèse.
3. **Résultats** : accessibilité, circulation supposée, sens et justification ; plus tard débit, pression et vitesse calculés.
4. **Présentation** : couleurs, flèches et animations. Un changement de filtre ou de caméra ne change pas les résultats du réseau.

### Vocabulaire proposé dans les deux interfaces

| Information | Valeurs ou formulation |
|---|---|
| Connexion | Relié à une arrivée / relié à un retour / isolé dans le scénario / données insuffisantes |
| Circulation | Circulation supposée / circulation non établie / indéterminée |
| Sens | Déduit / imposé par l'utilisateur / indéterminé / contradictoire |
| Provenance | Connecteur Revit / hypothèse utilisateur / règle topologique / calcul physique / mesure |
| Niveau du modèle | Indicatif / calculé avec hypothèses / comparé aux mesures |

L'affichage « sous pression » doit être réservé à un niveau qui permet effectivement de l'établir. « Fiable » doit préciser ce qui l'est : conformité aux données et règles, ou résultat physique validé. Une consigne manuelle ne prouve pas une réalité physique.

Chaque sélection doit pouvoir répondre : « D'où vient ce sens ? Quelles hypothèses l'expliquent ? Qu'est-ce qui manque ? Qu'est-ce qui a changé depuis le scénario de référence ? »

### Règles de cohérence

- Une vanne d'isolement fermée bloque ses passages définis ; un bypass peut maintenir une connexion.
- Une pompe oriente ou fournit une élévation de charge selon le niveau de modèle ; elle ne crée pas du fluide.
- Un diamètre peut aider une heuristique déclarée, mais ne prouve pas à lui seul le sens du débit.
- Une extrémité inconnue ne devient pas silencieusement un consommateur dans un mode de diagnostic rigoureux.
- Une direction inconnue ou conflictuelle n'est pas animée comme une direction établie.
- Les connexions internes des équipements multivoies doivent être décrites ; tous leurs ports ne constituent pas nécessairement un volume commun.
- Un graphe identique, un scénario identique et une version de moteur identique doivent produire des résultats identiques, indépendamment de l'interface et de l'ordre des clics. Si un état historique est réellement nécessaire, il devient une entrée explicite du scénario.
- Un résultat publié comporte la révision du modèle, celle du scénario, la version des règles et les hypothèses utilisées. La version du format JSON ne suffit pas à identifier le comportement du moteur.

## Cohérence technique Revit/web

Première étape : constituer un corpus commun de graphes et de scénarios, avec les résultats attendus par chemin. Comparer états, circulation, sens, raisons structurées et états de vannes, pas les images du rendu.

Le contrat web actuel est plus pauvre que le modèle C# : il ne décrit notamment pas les contraintes de direction du graphe ni les sections des connecteurs. Il faut formaliser les données nécessaires sans supposer que tout champ exporté est effectivement utilisé par le moteur web.

Ensuite, isoler un cœur de calcul sans dépendance à l'interface ou à Revit. La cible recommandée est un moteur de référence partagé ou un même service de calcul. Le choix entre exécution locale, WebAssembly et service doit être vérifié par un prototype prenant en compte l'usage hors ligne et les dépendances WPF actuelles. Deux implémentations restent possibles à court terme, sous contrôle de tests de parité obligatoires.

Pendant la transition, un recalcul web incomplet doit être identifié comme tel. Il ne doit pas conserver une ancienne explication de pompe ou de retour comme si elle venait d'être recalculée.

### Cas d'acceptation minimum

Chaîne simple ; vanne fermée et impasse amont ; té ; bypass ; boucle ; deux arrivées ; retour seul ; changement d'arrivée ; pompe et ports In/Out ; pompes parallèles contradictoires ; collecteur et petit piquage ; correction manuelle ; systèmes distincts ; échangeur à circuits séparés ; extrémité non renseignée ; absence de source ; export partiel.

Ajouter : même état final obtenu par plusieurs séquences d'actions ; permutation de l'ordre des éléments ; chargement/rechargement ; ancienne publication ; changement de révision rendant une hypothèse obsolète. Tous les résultats dérivés doivent correspondre à la même révision.

## Où aller ensuite ?

### Jalon 1 — Flux indicatifs honnêtes et cohérents

Harmoniser états, explications, affichage et règles Revit/web. Corriger les écarts P1, formaliser les limites de réseau et comparer automatiquement les moteurs.

Critère de sortie : aucun écart inexpliqué sur le corpus commun et aucune animation présentée comme un calcul de vitesse ou une preuve de pression.

### Jalon 2 — Diagnostic et analyse des coupures

Répondre à des questions concrètes : « Si je ferme cette vanne, quels équipements perdent leur connexion à une arrivée ? Existe-t-il un autre chemin ? Quels éléments de la maquette empêchent de conclure ? »

Ajouter une comparaison avant/après, la liste des équipements concernés, les vannes expliquant la coupure, les alternatives topologiques et un compte rendu partageable. Réutiliser les diagnostics, scénarios et traces déjà présents.

Un chemin alternatif établit une possibilité topologique, pas sa capacité à fournir le débit requis. Cette distinction reste visible. Un scénario numérique ne remplace pas une vérification d'isolement sur site.

Critère de sortie : résultat reproductible, hypothèses listées, anomalies localisables dans Revit et même compte rendu dans le viewer.

### Jalon 3 — Données nécessaires au calcul

Préparer les diamètres intérieurs, longueurs, matériaux/rugosités, altitudes, pertes singulières, caractéristiques des vannes, courbes des pompes, propriétés du fluide et conditions aux limites. Distinguer valeurs extraites, saisies, supposées et absentes. Normaliser les unités à l'entrée du calcul.

Afficher la préparation du réseau au calcul et les données bloquantes. Un débit de conception lu dans Revit doit rester identifié comme une donnée de conception, distincte d'un débit recalculé pour le scénario.

Critère de sortie : un circuit pilote dispose de données complètes et traçables ; les données absentes ne sont pas remplacées silencieusement par des valeurs donnant une fausse précision.

### Jalon 4 — Calcul stationnaire sur un domaine choisi

Commencer par un petit réseau d'eau sous pression explicitement compatible avec le solveur retenu. Calculer les débits, pressions, vitesses et pertes de charge, avec contrôle des bilans, des résidus et de la convergence. Comparer à des cas analytiques et à un outil de référence avant d'étendre le périmètre.

EPANET constitue un candidat à évaluer pour les réseaux d'eau sous pression : il prend en charge canalisations, nœuds, pompes, vannes et réservoirs. Cette piste n'établit pas sa compatibilité avec tous les circuits thermiques du bâtiment. Source : [EPA — EPANET](https://www.epa.gov/water-research/epanet).

La ventilation nécessite un modèle adapté à l'air et aux équipements concernés ; les réseaux gravitaires nécessitent également des règles distinctes. La référence EnergyPlus distingue la pression aux nœuds et le débit dans les liaisons d'un réseau d'air : [EnergyPlus, Engineering Reference 24.2](https://energyplus.net/assets/nrel_custom/pdfs/pdfs_v24.2.0/EngineeringReference.pdf).

Critère de sortie : erreurs numériques quantifiées sur les références, limites documentées, résultats invalides clairement signalés. Ne pas transformer le potentiel visuel actuel en bars par une simple conversion d'échelle.

### Jalon 5 — Exploitation et comparaison au réel

Rattacher les équipements BIM aux mesures de débit, pression, température et états de commande, avec horodatage et qualité de donnée. Comparer calcul et mesure, calibrer les paramètres et signaler les écarts.

Ce n'est qu'à ce stade qu'une trajectoire vers un jumeau numérique d'exploitation devient concrète. La représentation 3D animée seule ne fournit ni observation du réel ni validation du modèle.

## Ordre de travail recommandé

1. Formaliser les mots et règles communs, notamment arrivée/retour, impasse et sens inconnu.
2. Fixer le corpus de parité et les cas de divergence constatés.
3. Harmoniser le recalcul web, les fiches et l'invalidation des résultats ; versionner les règles.
4. Livrer l'analyse d'impact des coupures avec comparaison de scénarios.
5. Choisir un circuit pilote et préparer ses données pour un calcul stationnaire.

Le jalon 2 est la prochaine valeur métier à viser. Le jalon 4 doit être un projet de calcul avec un domaine, des données d'entrée et une validation propres.

## Repères dans les sources

- `BIMaestro/commands/Jeux vidéos/Maquette jouable/GameMepData.cs` : états et données, `Recalculate`, `ApplyCirculationPotential`, `GetRelaxationWeight`.
- `RevitGameMepExtractor.cs` dans le même dossier : `DetectValve`, `ScoreSource` et extraction des connecteurs.
- `GameMepFlowRenderer.cs` : vitesse visuelle et filtrage des chemins animés.
- `GameMepDirectionExplanation.cs` : provenance des sens.
- `GameMepWebPackage.cs` et `GameMepReplayStore.cs` : export du graphe et de son résultat.
- `BIMaestro.MepSimulation.Tests/Program.cs` : cas de régression existants, notamment stagnation amont et retour seul.
- Viewer version 33 : `lib/mep-engine.ts`, `lib/mep-engine.test.ts`, `lib/mep-contract.ts`, `components/mep-viewer.tsx`.
