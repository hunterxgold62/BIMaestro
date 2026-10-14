# Mesures du suivi automatique

Les journaux JSONL sont écrits par le worker historique dans `history-performance`
sous le dossier historique actif. Un fichier par session et jour contient plusieurs
maquettes, distinguées par ModelKey et RuntimeDocumentId (y compris non enregistrées).
Le chargement léger traite la maquette active ; revenir sur une autre maquette reprend
sa file indépendante. Le préchargement général attend 2 secondes sans entrée utilisateur
ET avec Revit au premier plan, puis au moins 2 secondes entre passages.
Un retour au premier plan impose à nouveau 2 secondes de délai pour ce préchargement.
La sélection explicite démarre sans ce délai, via ExternalEvent, en courts passages
espacés de 100 ms minimum. Elle reste limitée aux éléments sélectionnés et ne travaille
pas hors de Revit. Stop suspend toutes les préparations ; Reprendre conserve et reprend
les files et les données capturées. Un appel Revit engagé reste non interruptible.

Les lots légers visent 25 objets / 25 ms et attendent 25 ms entre lots.
Ce premier passage conserve les données de suivi ET la recette de restauration
directe pour les objets pris en charge, sans capturer les jonctions, connexions,
circuits supplémentaires ni lancer les copies natives. Les profils et placements
nécessaires à la reconstruction restent prioritaires. Le catalogue initial filtre
les catégories de modèle, plutôt que parcourir tous les éléments internes du projet.
Les paramètres d'un type sont
réutilisés pour les occurrences du même document ; toute transaction du document
invalide ce cache pour ne pas masquer une modification de type. La boîte englobante est lue une seule
fois pour ses deux extrémités.

Une file distincte enrichit ensuite les recettes (liaisons, dépendances, détails et
demande d'archives complexes), par passages de 25 ms maximum visé, sélection prioritaire.
Sans sélection à préparer, l'enrichissement et les copies natives attendent la fin
de la couverture de base. La sélection est une exception explicite : ses captures
manquantes passent avant le scan général, puis ses enrichissements et archives.
Tous ses snapshots légers sont préparés avant ses enrichissements complexes.
Un indicateur modeless affiche les recettes disponibles et les préparations restantes.
Une nouvelle sélection consulte le cache avant le budget de capture : elle ne
remplace pas les snapshots existants. Désélectionner ne supprime pas les données
ni le travail déjà en attente. Une nouvelle sélection reprend le travail manquant.
Cette séparation ne rend pas la sauvegarde native RVT interruptible.
La file vérifie que le snapshot visé est
toujours celui en mémoire : une modification ou suppression invalide le travail
ancien. Les grandes sélections ne font plus toutes leurs captures/restaurations
synchrones : après 80 ms visés, les objets sans snapshot passent dans une file
de capture différée. Une suppression immédiate peut donc précéder leur capture.
Le détail de maillage optionnel est lui aussi différé pour les objets sélectionnés.
`PreloadAtDeletion.Complete` décrit la couverture de base, pas la fin de l'enrichissement
ni la fin de l'archivage complexe. `RestorationCapturesPendingAtDeletion` compte
désormais les enrichissements en attente, pouvant déjà avoir une recette directe.
Les audits contiennent aussi
`SelectionCapturesPendingAtDeletion` pour distinguer ces états.
La vue active réordonne les candidats restants. Un DispatcherTimer de 250 ms demande
un ExternalEvent lorsque Revit est au premier plan et inactif ; seul Execute lit
la maquette. Le suivi ne relance plus Idling en boucle avec SetRaiseWithoutDelay.
Chaque passage vérifie que la maquette active possède une baseline programmée,
y compris après Enregistrer sous ou si elle était déjà ouverte.
L'état d'inactivité est contrôlé avant chaque capture et chaque copie
native : une nouvelle entrée arrête l'enchaînement après l'appel Revit en cours.
Le contrôle Windows détecte les entrées de toute la session, y compris dans une autre
application ; il ne se limite pas à la fenêtre Revit.
Une collecte visible peut déclencher du travail Revit coûteux : sa durée est mesurée.
Le callback Idling vise 25 ms de travail historique avant de lancer un autre passage,
avec 100 ms de pause entre passages. Une phase longue empêche l'empilement des autres
phases dans le même callback. Les archives natives traitent au maximum 16 racines,
visent 50 ms de copies automatiques (250 ms en test explicite) et attendent 500 ms
entre vagues (jusqu'à 5 secondes de récupération après une vague longue). Ces budgets sont coopératifs : un appel
Revit ne peut pas être interrompu et peut les dépasser.
Un document d'archive est réutilisé tant que le modèle source n'a pas changé,
pendant au maximum 8 vagues ou 32 racines. Une archive dépassant 8 Mio est libérée
avant la vague suivante pour éviter de réenregistrer une grosse géométrie avec chaque
petit objet suivant. Chaque vague enregistre un nouveau fichier
immuable. Un changement du modèle source, un changement de maquette ou 30 secondes
sans travail restant libère le document d'archive. Les anciens fichiers restent utilisables.

Ouvrir plusieurs maquettes représentatives, alterner les vues, sélectionner des objets,
puis laisser des pauses de plus de 2 secondes. Bouger ensuite la souris pendant les
captures pour vérifier que l'enchaînement s'arrête après l'opération en cours.
Garder le mode historique identique
pour comparer les modèles et versions. Les tests sur copie de la maquette Testi mesurent
le travail API et la restauration d'une sélection historique ; ils ne mesurent pas
le temps total du suivi automatique ni les pauses ressenties pendant la navigation.

Exemple :

```powershell
./scripts/history-tests/SummarizePerformance.ps1 -LogDirectory 'CHEMIN/history-performance' -OutputCsv './tmp/history-performance.csv'
```

Les phases sont `preload`, `view_priority`, `native_view_priority`, `native_archive`
et `native_root`, ainsi que `restoration_capture` et `selection_capture`. Le temps
restoration_capture inclut les captures de sélection différées du même passage :
ne pas additionner ces deux phases. Elements compte les candidats visités pour preload, les visibles
pour les collectes de vue, les racines sorties de file pour native_archive (réussite
ou échec) et les racines tentées pour native_root (Success distingue réussite/échec). Remaining mesure
la file restante de la phase ; Complete décrit sa vidange, pas une garantie de
restauration de tous les objets. La phase native_root est incluse dans native_archive :
ne pas additionner leurs durées. P95, MaxMs et les nombres de passages >100 ms / >1 s
permettent d'évaluer la gêne, indépendamment du temps total de préparation.
ObservedSeconds comprend les pauses, la navigation et le temps passé dans les autres
maquettes. Les fichiers contiennent le nom et la clé de maquette ; ils restent dans
le dossier historique existant, éventuellement partagé, et ne sont pas envoyés ailleurs.

## Identifier les objets coûteux

Schéma 2 : `element_capture_basic`, `element_capture_full`, `element_enrichment`
et `native_root` incluent ElementId, ElementUniqueId, ElementClass, Category,
ElementName, ElementFamily, ElementTypeId, ElementType et Success. Les durées de capture de base sont enregistrées
même lorsque la capture lève une exception. Success indique l'achèvement de cette
opération, pas une garantie de restauration complète. Les copies de parents agrégés
sont identifiées par leur racine (par exemple l'escalier d'un garde-corps hébergé).
Création de document, préflight des types et sauvegarde restent mesurés séparément.

```powershell
./scripts/history-tests/SummarizeElementCosts.ps1 -LogDirectory 'CHEMIN/history-performance' -OutputCsv './tmp/element-costs.csv'
```

Le rapport classe par maximum observé et donne moyenne, P95, occurrences dépassant
100 ms / 1 s et l'identité de l'objet le plus lent. `-ModelKey` permet de limiter
le rapport à une maquette. Les anciens journaux sans identité d'objet sont ignorés.
Ne pas additionner les mesures individuelles à leurs lots englobants.

Les enrichissements et copies natives sont ordonnés par coût, à l'intérieur de leur
priorité de sélection. Une estimation provisoire repousse les classes complexes ;
ses valeurs sont des poids de classement, pas des temps garantis. Les durées mesurées
ajustent ensuite le classement par document/type/phase pendant cette session.
Une capture de base déjà mesurée au-delà de 100 ms est reportée derrière les autres
candidats, une seule fois, pour ne pas l'écarter définitivement. Elle est également
évitée dans le passage synchrone de sélection. Le classement lit des valeurs en
mémoire et ne parcourt pas la géométrie pour prédire son coût.
Les profils ne sont pas rechargés depuis le disque à la réouverture. Un premier
objet inconnu peut encore provoquer une pause longue ; l'API engagée reste indivisible.

## Ajustement du 13 octobre : terrains et lots inactifs

L'archivage automatique exclut Toposolid et TopographySurface, ainsi que leurs
groupes agrégés contenant un terrain. Leur suivi léger reste présent, mais sans
promesse de restauration après suppression. Une archive déjà prête reste réutilisable.
Les appels explicites de validation gardent la possibilité de capturer les terrains.

L'inventaire inactif examine au maximum 100 candidats par lot, avec un budget de
500 ms contrôlé entre éléments (150 ms avant l'ajustement du 14 octobre demandé
par l'utilisateur ; initialement 25 candidats / 25 ms). Le passage global
ne démarre d'autres étapes que si moins de 200 ms se sont écoulées, puis laisse au moins 2 secondes
de pause. Une API individuelle ne peut toujours pas être interrompue.
La préparation explicite de sélection conserve ses petits passages immédiats.
Son affichage sépare maintenant les recettes manquantes en préparation des éléments
déjà préparés dont les compléments sont encore en cours, et signale les terrains exclus.

Le journal utilisateur `performance-2026-10-13-10b8ab1feda54875ba97354950c0856a.jsonl`
montre un archivage de terrain de 17,011 s, dont 14,365 s de copie native.
Les mesures englobantes et individuelles ne doivent pas être additionnées.
Le préchargement avait bien des passages espacés d'environ 2 s pendant les périodes
éligibles, mais ceux-ci ne visitaient que 1 à 25 candidats. Les interruptions de
cadence ne suffisent pas à identifier leur cause sans une mesure des conditions d'arrêt.
