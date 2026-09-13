# Correction du réseau Testi crousty — moteur 2.0.1

Viewer version Sites 35 publié avec succès : https://bimaestro-mep-viewer.hunterxgold.chatgpt.site. Commit `673049a57bbdc2d28fe3754a66726d5e184356d9` ; déploiement `appgdep_6aa5a6672a7c81918c54d3614111ca68`.

Cas fourni : export du 12 septembre 2026 à 20:55:31, 986 éléments, 2030 connecteurs, 1099 chemins. L'export original reste dans les Documents de l'utilisateur ; il n'a pas été publié sur le site.

## 827886 : sortie du filtre sans circulation

Dans l'export, le tronçon est relié à une arrivée et à un retour mais `HasCirculation` vaut faux. Le filtre 810046 possède cinq ports, dont deux seulement sont raccordés : 64 (In) et 65 (Out). La règle multivoie de la version 2.0.0 bloquait tous ses passages en l'absence de correction manuelle. C'était une régression trop restrictive.

La version 2.0.1 reconnaît l'unique paire native In/Out parmi les ports raccordés. Les trois autres ports ne sont pas reliés arbitrairement au passage principal. Le test confirme que 827886 circule à nouveau avec le scénario fermé de l'export, notamment le bypass 828068.

## 849383 : sens du collecteur de refoulement

Le tronçon était imposé dans le sens 707 → 708 par une continuité géométrique issue du té 824162, propagée jusqu'aux tés des pompes. Cette heuristique pouvait prendre le dessus sur la fonction locale du collecteur. De plus, le fond bombé 849534 n'était pas qualifié comme bouchon et pouvait servir de débouché implicite.

La version 2.0.1 reconnaît les bouchons/fonds bombés à un port. La continuité géométrique s'arrête aux branches ancrées par un refoulement de pompe. Une règle de continuité locale utilise exclusivement les sens de pompe établis : lorsqu'il reste un seul passage actif au té, son sens est opposé aux autres entrées ou sorties connues. Elle ne fait pas de vote majoritaire selon le nombre de pompes.

Le rejeu donne maintenant **708 → 707**, avec circulation sur 849383. La même sortie est reproduite après ouverture puis fermeture du bypass.

## Validation et limites

- 460 assertions générales et 6 assertions dédiées à cet export réussies.
- 196 comparaisons générales et 6 comparaisons du modèle complet entre C# et navigateur : zéro écart, y compris permutations. Les parcours sont désormais ordonnés de manière canonique, sans changer les indices de connecteurs.
- DLL Release2024 compilée. SHA256 : `F80A78292CAF28B0C1EC7B0D4E9124A8D9FC12CA3022F0C25E765B3A43DAC40B`.
- Une autre rupture visible du rejeu reste signalée entre le filtre 859327 et la bride 913935 ; elle n'est pas présentée comme corrigée par ce travail.
- Validation par rejeu des données et exécution du moteur navigateur ; pas de nouvelle session Revit interactive effectuée par l'agent.

Commande locale dédiée : `BIMaestro.MepSimulation.Tests/bin/Debug/BIMaestro.MepSimulation.Tests.exe --verify-crousty CHEMIN_EXPORT`. Le corpus de ce modèle est généré uniquement dans `tmp/mep-real-parity.json`.

Après chargement de la DLL corrigée, rouvrir Maquette MEP pour recalculer le graphe. Pour les comparaisons avant/après, choisir une nouvelle référence compatible avec le moteur 2.0.1. Republier la maquette transmettra les nouvelles qualifications aux consultations web.
