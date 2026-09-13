# Livraison des jalons 1 et 2 — 12 septembre 2026

Viewer version Sites 34 publié avec succès : https://bimaestro-mep-viewer.hunterxgold.chatgpt.site. Déploiement `appgdep_6aa59aa1c908819192af59ef29433e05`.

## Fonctionnement livré

Le plugin et le viewer exécutent les mêmes sources C# du moteur `mep-topology-2.0.0`. Le navigateur utilise une compilation WebAssembly locale. L'ancien recalcul TypeScript simplifié a été supprimé. Aucun graphe n'est envoyé à un service de calcul.

L'accessibilité à une arrivée et à un retour est distincte de la circulation supposée. Une impasse ne reste plus animée uniquement parce qu'elle est connectée à une arrivée. Les flèches exigent une circulation et un sens résolu. Les résultats et explications précédents sont masqués pendant le recalcul et en cas d'échec.

Le panneau « Analyser les coupures / comparer » du plugin et « Analyser les coupures » du viewer propose une référence avant/après, les équipements concernés, les vannes fermées sur le chemin de référence, un chemin alternatif lorsqu'il existe, des diagnostics localisables et le même compte rendu texte. La référence choisie dans le viewer est locale à la consultation ; les hypothèses du scénario sont partagées.

Les extrémités peuvent être qualifiées : à vérifier, terminal, retour, limite d'export ou bouchon. Le mode explicite retire l'hypothèse de débouché pour les extrémités non renseignées. Ces réglages sont conservés dans le scénario Revit et son historique, et modifiables sur le web avec un lien d'édition. Les écritures web vérifient la révision du modèle et du scénario.

L'extraction ne valide plus automatiquement les organes spécialisés comme des vannes d'isolement. Les composants multivoies autres que les jonctions reconnues signalent leurs passages à vérifier ; seuls des couples de connecteurs explicitement imposés peuvent les traverser. Cela ne simule pas la loi physique d'une soupape ou d'une vanne de régulation. Une ancienne publication doit être régénérée pour bénéficier des nouvelles qualifications extraites et des identifiants d'extrémités.

## Vérifications effectuées

- 458 assertions du programme de régression MEP réussies, avec contrôles d'export GLB, intégrité et métadonnées.
- 194 comparaisons du résultat C# natif et WebAssembly : états, sens, raisons, faces des vannes et compte rendu identiques, y compris permutation des éléments et connexions.
- 30 tests du viewer réussis ; TypeScript et lint vérifiés ; compilation de production réussie.
- Parcours navigateur sur un paquet réellement produit par l'exporteur : chargement, hypothèses, coupure, compte rendu téléchargé, changement de référence et contrôles en lecture seule.
- API `mep-share` version 14 déployée : droits, validation, révisions périmées et deux écritures concurrentes testés sur une publication temporaire, supprimée après vérification. Les autres données du scénario sont conservées.
- Compilation Release2024 réussie. Les avertissements d'API Revit obsolète concernent des commandes existantes hors de cette modification.

Pas d'essai interactif dans une session Revit ni de validation sur une maquette de production. Les résultats restent topologiques et indicatifs : aucun débit, pression ou vitesse physique n'est calculé. Les chemins alternatifs sont des témoins de connexion, pas une preuve de capacité ni une liste exhaustive de toutes les possibilités.

## Utilisation

1. Charger la DLL `BIMaestro/bin/Release2024/BIMaestro.dll` dans l'installation Revit 2024 habituelle, puis redémarrer Revit si nécessaire.
2. Ouvrir Maquette MEP et le panneau d'analyse, vérifier les limites et les composants signalés.
3. Prendre une référence, fermer une vanne et consulter les changements. Un double clic dans le rapport Revit localise l'élément ; le viewer propose des boutons de localisation.
4. Republier la maquette pour transmettre les nouvelles métadonnées, puis comparer le même scénario dans les deux interfaces.

## Maintenance

Le viewer se trouve dans `.sites/bimaestro-viewer-mep-audit`, dépôt Sites distinct. Son commit livré est `46abecd58d3f41bdabea706b34ea154934d1087b`.

Après modification du moteur partagé : publier `BIMaestro.MepEngine.Browser` avec `dotnet publish -c Release -o tmp/mep-browser-publish`, exécuter `node scripts/sync-mep-browser.cjs`, régénérer le corpus avec le programme de tests `--export-parity tmp/mep-parity.json`, puis lancer `node scripts/test-mep-browser.cjs --permutations`. Le manifeste du runtime enregistre les empreintes des sources partagées. Recompiler le plugin et reconstruire le viewer avant publication.

Le test d'interface `node scripts/test-mep-impact-ui.cjs` utilise le serveur local sur le port 3000 et le paquet de test généré avec le corpus. Il intercepte les échanges réseau et ne modifie aucune publication réelle.
