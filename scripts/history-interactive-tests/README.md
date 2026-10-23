# Observation interactive Antibes — Revit 2025

Cet addin de diagnostic est distinct du produit et des anciens bancs. Il charge
une copie locale uniquement dans le PID lancé pour ce run. Il observe les vrais
hooks de BIMaestroApp et l’assemblée effectivement installée ; il n’appelle jamais
manuellement le scheduler, `BuildSnapshot`, `PrimeDocument` ou `EnqueueDeleted`.
Le dossier du journal est redirigé uniquement en mémoire vers `run/history`, sans
modifier la configuration persistante ni le manifeste BIMaestro utilisateur.

```powershell
./scripts/history-interactive-tests/Prepare.ps1 -RunName antibes-interactive-v1
./scripts/history-interactive-tests/Launch.ps1 -RunName antibes-interactive-v1
```

Le lancement est laissé à l’opérateur. Le lanceur ajoute temporairement son seul
manifeste `BIMaestro.History.Interactive.addin`, démarre une nouvelle instance
Revit 2025 et retire ce manifeste après `ready.json`. Le PID, le chemin et le SHA
de la DLL réellement chargée sont enregistrés. Les autres processus restent intacts.

## Protocole par fichiers

Dans `tmp/history-interactive/antibes-interactive-v1` :

- Écrire `open.request` quand l’opérateur est prêt à naviguer. Un ExternalEvent
  ouvre et active `Antibes-interactif.rvt`. Aucun modèle ne s’ouvre auparavant.
- Dès le premier viewport visuellement utilisable, écrire `viewport-ready.request`.
  Ce marqueur est humain : `DocumentOpened`, `ViewActivated` et le premier Idling
  actif ne prouvent pas à eux seuls qu’une image utile a été affichée.
- Écrire `navigation`, `selection`, `restauration` ou un autre nom dans `stage.txt`
  pour annoter les phases ; utiliser ensuite de vrais gestes de navigation Revit.
- Deux suppressions sont automatiquement demandées une et trois secondes après
  `DocumentOpened` : modèle générique ID 474717, puis plante ID 520659. Le délai
  réel d’exécution est enregistré, car Revit peut différer un ExternalEvent pendant
  une commande utilisateur. La présence du snapshot et l’état de la copie sont
  relevés avant chaque suppression ; aucune capture produit artificielle n’a lieu.
- `select.request` contenant un UniqueId appelle seulement la sélection Revit,
  puis l’opérateur peut appuyer réellement sur Suppr. Cible IPN déjà éprouvée :
  `2f0d3229-e77f-4d56-8aca-0a52aee986c1-000f6c7f`. La sélection conserve les données
  initiales de vérification sans préparer manuellement l’historique.
- Écrire `restore.request` pour relire les suppressions réellement persistées par
  le worker et appeler la vraie restauration de production. L’absence de données
  persistées n’est pas contournée par un flush forcé. Réécrire ce fichier permet
  un nouvel appel après une autre suppression UI.
- Écrire `finish.request` après la navigation et les vérifications. L’ExternalEvent
  exporte les dernières données, active un document vide et ferme la copie **sans
  enregistrer**. Le PID reste ouvert sur le document vide ; aucune fermeture
  automatique n’interrompt l’opérateur. `finished.txt` confirme cette étape.

Exemple d’écriture depuis la racine du dépôt :

```powershell
$run = Join-Path (Get-Location) 'tmp/history-interactive/antibes-interactive-v1'
[IO.File]::WriteAllText((Join-Path $run 'stage.txt'), 'navigation')
[IO.File]::WriteAllText((Join-Path $run 'open.request'), [DateTime]::UtcNow.ToString('O'))
```

## Mesures et limites

`observer.jsonl` contient les événements bruts horodatés, les changements de vue,
les entrées utilisateur Windows, les passages Dispatcher et les états de
production. Les sondes API d’état passent au plus quatre fois par seconde pendant
Idling et leur coût propre est enregistré. Les fichiers et requêtes sont traités
hors thread Revit ; aucun worker ne lit une API Revit.

Un DispatcherTimer de priorité Background vise 25 ms. Ses écarts incluent le rendu,
les commandes Revit, la priorité des messages, les autres addins et le système.
Ils ne sont ni des temps de frame peinte ni une mesure exclusivement attribuable
à BIMaestro. L’âge de la dernière entrée au prochain passage peut perdre des
entrées intermédiaires pendant une séquence rapide. La vidéo/capture et les gestes
réels de l’opérateur complètent donc ces indices.

`journal_delete_first_seen` mesure une borne haute entre le timestamp produit et
la première lecture disque, avec un polling de 100 ms. Les durées de phases
`history/history-performance` sont celles du produit. Les phases imbriquées ne
doivent pas être additionnées ; un long intervalle Idling ne prouve pas un blocage
du plugin.

`restore_verification` compare la classe, l’UID de type et le point ou les trois
points de l’axe à `1e-5` pied, à partir de métadonnées relevées avant suppression.
Les données de connexions et d’échec détaillées restent dans le résultat réel de
restauration. Les deux cibles automatiques et l’IPN ne possèdent pas de connexion
MEP à reconstituer. Aucune généralisation aux autres géométries n’est prétendue.
