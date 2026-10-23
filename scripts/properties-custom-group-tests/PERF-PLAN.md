# Mesurer la réactivité du prototype r8

La priorité est désormais la latence de l'interface. La qualification r7 est gelée aux trois paires admissibles : Limite de pièce, liste de niveau et texte partagé métier. Le premier essai du booléen partagé n'est pas qualifié. Les observations r6 restent séparées ; aucun nouveau test fonctionnel n'est déduit d'une mesure de vitesse.

## Coûts identifiés par lecture du code r7

| Chemin | Travail sur le thread UI | Fréquence observée dans le code |
|---|---|---|
| `Ensure → HasNativeHeader` | `RefreshCount` énumère tous les modules du processus ; un second parcours retrouve DesktopMFC ; lecture des libellés de toute la grille. | À chaque tick actif, cible 500 ms. |
| `Ensure → VerifyCheap` | Nouvelle énumération des modules via `RefreshCount`, allocation d'un dictionnaire de rangs et vérification des 29 paramètres natifs visibles. | Même tick : trois énumérations de modules au total dans le chemin stable. |
| `NativePropertiesGrid.Find` | Parcours des fenêtres natives pour retrouver GXWND ; les sources de production possèdent déjà `RefreshIfCurrent`, mais Ensure ne réutilise pas ce cache. | À chaque Ensure, même si le HWND/Core n'a pas changé. |
| `DiagnosticTick → NativeState` | Parcours des modules, résolution d'exports et de delegates, fenêtres enfant/focus, sérialisation et écriture JSON. | Timer UI d'une seconde, après le premier rapport. |
| `Run(group-insert)` puis `Run(group-members)` | Trois modules lus et hachés à chaque commande ; quatre empreintes complètes du modèle pour la paire de commandes. | Chaque réapplication automatique aussi. |
| `Verify` de laboratoire | Relecture des cellules, correspondances, allocations et écriture d'un rapport de chaque étape. | Dix étapes pour une séquence de création/transfert. |
| `Report` | Snapshot de 18 550 valeurs, snapshot de 18 550 descripteurs, tri/sérialisation/hash et écriture d'environ 6,1 Mo puis copie. | Toute commande standard appelle Report avant de rendre la main. |
| `SetRaiseWithoutDelay` | Idling demandé en continu, y compris pendant les retours immédiats du throttle. | Même si aucun Ensure ni aucune commande n'est utile. |

Les trois modules représentent 37 712 560 octets sur ce poste (DesktopMFC, Utility, Objective Grid). Deux `Run` relisent donc environ 75,4 Mo pour les hashes, en plus du travail natif et des captures métier. C'est un comptage statique des appels et des tailles, pas une attribution de temps CPU mesurée.

Les horodatages internes r7 lus pendant l'audit montrent environ 661–667 ms entre deux rapports de déplacement consécutifs. Cet intervalle comprend le déplacement, la vérification et les coûts de laboratoire ; **ce n'est pas le temps isolé de MoveRows ni la latence d'un clic**. L'espacement régulier rend les recherches répétées de modules particulièrement suspectes. Il faut les mesurer avant de supprimer ou d'affaiblir une garde de sécurité.

## Modes et commandes proposées pour r8

- `audit-full` : activer les captures complètes explicitement, hors intervalle mesuré. Sert à la baseline et à la vérification métier finale ; toutes les écritures de paramètres restent faites dans Revit.
- `audit-fast` : aucune empreinte de modèle, aucun `Report`, aucun schéma ni fichier d'étape complet sur le chemin des interactions mesurées. Les guards natifs, la vérification d'identité, le refus pendant saisie et la limite de récupération restent actifs. Retour compact de la commande.
- `perf` : produire seulement un petit paquet de mesures cumulées, mode, PID, hôte, état natif du groupe et compteurs. Cette commande ne déclenche ni `Report`, ni Snapshot, ni Schema, ni reconstruction. Écrire le paquet après la fenêtre de mesure.

Une mesure doit indiquer explicitement si le timer diagnostic est actif. Pour la comparaison principale, le désactiver dans les deux conditions ; le réactiver séparément pour mesurer son propre surcoût. Le cache natif doit être initialisé et vérifié avant la fenêtre mesurée. Les handles/delegates mis en cache restent liés au module et à la grille courante ; une invalidation native doit toujours empêcher la réutilisation du mauvais contrôle.

## Protocole comparable

1. Conserver le même hôte Revit, le même projet jetable, la même vue, le même mur et le même état de paramètres. Préparer la palette, initialiser les caches, puis effectuer un audit complet en dehors de toute mesure.
2. Vérifier avant chaque fenêtre : groupe natif reconnu, six membres dans l'ordre attendu, toutes les identités concernées valides, aucune saisie active. Ne pas confondre un drapeau `membersReady` et une relecture native réelle.
3. Passer `audit-fast`. Noter les compteurs initiaux. Ne pas lancer `report`, `baseline`, `verify-write`, `verify-unchanged` ou une capture complète dans cette fenêtre.
4. Mesurer séparément : (a) 30 s au repos avec mur sélectionné, (b) une série fixe de dix transitions vue → mur, (c) une série fixe d'ouvertures/validations de l'éditeur. Les commandes de sélection mesurées doivent retourner un accusé compact ; aucune copie de snapshot ne doit y être couplée.
5. Première condition : palette native sans groupe expérimental ni récupération automatique. Deuxième condition : groupe personnalisé et récupération r8 en mode rapide. Une troisième comparaison éventuelle distingue r7/r8 ; elle n'est comparable que si r7 dispose lui aussi d'un mode rapide équivalent. Les anciens logs instrumentés lourdement ne constituent pas une baseline de clic valide.
6. Capturer `perf` seulement à la fin de la fenêtre. Vérifier ensuite le groupe natif et les six membres. Repasser `audit-full` pour comparer le modèle/schéma et garder cette capture hors timings de l'interaction.
7. Répéter les mêmes fenêtres après échauffement. Publier nombre d'observations, médiane si les échantillons bruts existent, P95 et maximum. Ne pas moyenner les P95 de plusieurs fenêtres ; présenter les fenêtres ou recalculer depuis les échantillons bruts.

Le changement de vue peut temporairement afficher une palette ancienne ou le navigateur. Si le groupe n'est pas retrouvé et vérifié à la fin, la fenêtre reste une observation de performance, **pas une preuve d'un groupe utilisable**. La mesure doit conserver le nombre de réapplications, refus, échecs, reconstructions et arrêts automatiques.

## Mesures légères utiles

Enregistrer `Stopwatch.GetTimestamp` avant/après `Ensure`, `HasNativeHeader`, `VerifyCheap`, découverte de grille, chaque primitive native d'insertion/déplacement et `Run` entier. Stocker les durées et compteurs dans une structure bornée en mémoire ; pas de JSON, fichier, énumération de modules ou lecture DB pendant la collecte. Les coûts de vérification restent distingués de la primitive native.

Pour la latence de l'interface, séparer : délai de prise en charge d'un message d'entrée, ouverture effective de l'éditeur natif et délai d'affichage de la frame suivante. Un observateur natif minimal peut mémoriser uniquement timestamp/message/HWND dans un tampon, puis exporter hors intervalle. Une mesure extérieure basée sur une commande CU inclut le délai d'automatisation ; l'étiqueter « latence observée de bout en bout », pas « durée interne Revit ». La dérive d'un timer Dispatcher mesure la disponibilité du thread UI, pas directement la latence d'un clic.

Les optimisations prioritaires à vérifier sont : modules/exports/delegates validés une fois, grille réutilisée tant qu'elle est courante, rangs de tokens calculés lors des mutations, arrêt du timer de diagnostic en mode rapide et captures métier déplacées hors interaction. Conserver les contrôles des 29 identités si leur coût devient faible après cache ; ne pas gagner du temps en supprimant prématurément la protection des lignes voisines.

## Paquet compact pour le collecteur

```json
{
  "command": "perf",
  "pid": 12345,
  "version": "2025",
  "build": "25.4.41.14",
  "auditMode": "fast",
  "scenario": "view-wall-10cycles",
  "fixtureId": "identité stable du document",
  "diagnosticTimerEnabled": false,
  "group": {"headerNative": true,"memberCount": 6,"mappingMatches": true},
  "metrics": {"CellReads": 0,"Timings": {"Ensure": {"Count": 10,"MeanMs": 1.0,"P95Ms": 1.5,"MaxMs": 2.0}}},
  "counters": {"reapplications": 1,"failures": 0,"automaticStops": 0}
}
```

`CollectPerf.py` accepte ce paquet et les noms de métriques supplémentaires. Il conserve les agrégats déclarés, signale les captures complètes accidentelles et n'invente ni échantillons de clic, ni comparaison contrôlée. Les champs de contexte manquants apparaissent comme limites. Aucun résultat chiffré d'amélioration n'est encore revendiqué dans cette note.
