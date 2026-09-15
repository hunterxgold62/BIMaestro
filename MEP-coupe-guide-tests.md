# Coupes par faces — Maquette BIM et viewer web

Cliquer **Coupes / Coupe**, puis une face visible du bâtiment. La normale de la face
définit le plan, y compris pour une face inclinée. La partie située derrière la face
est conservée. **+ Cliquer une face** permet d'ajouter jusqu'à huit coupes simultanées.

Sélectionner une coupe dans la liste pour la déplacer avec **Ctrl + molette** (10 cm
par cran) ou le curseur de décalage depuis la face. Chaque coupe peut être désactivée,
inversée ou supprimée indépendamment. **Tout réafficher** retire toutes les coupes.
Échap annule la sélection d'une face. Ces réglages sont locaux à la session.

Les coupes sont visuelles : aucun élément Revit, graphe MEP ou état de vanne n'est
modifié. Les collisions de marche restent celles du bâtiment ; le vol permet de
visiter les volumes coupés. Aucune vue de coupe Revit n'est créée.

## Vérification visuelle

1. Cliquer successivement deux faces perpendiculaires, puis une face inclinée.
   Les plans doivent passer par les points cliqués et conserver leur orientation.
2. Déplacer et inverser une seule coupe : les autres restent en place.
3. Tester le curseur et Ctrl + molette. Les mouvements de coupe ne doivent pas zoomer.
4. Cliquer à travers les volumes masqués : seuls les triangles conservés sont sélectionnés.
5. Activer les flux et animer une porte : lignes, flèches et portes sont tronquées aux plans.
6. Désactiver puis supprimer les coupes : les portions masquées réapparaissent.
   Dans le plugin, le rendu de base d'origine est réutilisé dès qu'aucune coupe n'est active.
7. Dans le site, vérifier le même parcours avec une publication chargée par zones.
   Les annotations respectent toutes les coupes. Les ombres sont suspendues pendant
   les coupes pour éviter celles de toitures ou murs masqués.

## Contrôles automatisés

- C# : normales des triangles de sélection, intersection de plusieurs demi-espaces,
  segments de flux traversants, rayons parallèles, inversion et restauration.
- Web : coupe à partir d'une face inclinée, décalage, intersection de plusieurs plans,
  désactivation, suppression et remise à zéro.
- Compilation Release du plugin, TypeScript, Vitest et build de production du site.

## Diagnostic de 944916

Avec l'export Antibes du 14/09/2026 à 15:52:46, les tronçons 929913, 944840, 944916,
1003351 et 930914 sont en circulation avant et après le recalcul C#.
Le calcul web conserve aussi cette circulation. Les essais modifiant chaque vanne
individuellement n'ont pas reproduit la stagnation ; les essais C# modifiant chaque
source individuellement non plus. Le fichier d'origine n'a pas été modifié.

L'export de 17:29:50 reproduit le défaut : la géométrie de 929913 a été modifiée
et les ports ont permuté. L'arrivée mémorisée 106 → 107 pointe désormais vers
le bout libre ; le raccordement au réseau est au port 106. Ce blocage de frontière
élague le tronçon jusqu'au té 1003350. La mention « devant une vanne fermée »
était une explication générique de cet élagage, pas la preuve d'une vanne locale.

Le moteur réaligne maintenant les arrivées manuelles sur une canalisation à deux
ports avec un seul bout raccordé et un bout libre non qualifié : entrée côté libre,
sortie côté réseau. Les frontières intérieures, équipements et extrémités déclarées
ne sont pas réorientés par cette règle. Elle s'applique à chaque recalcul, y compris
après restauration d'un scénario et au rejeu des anciens exports.

Validation : 479 assertions générales, 16 sur le nouvel export et 1674 sur l'ancien
cas 952560. Dix chemins reprennent la circulation, aucun chemin déjà circulant
ne s'arrête. Le résultat est stable au deuxième recalcul. Une copie de diagnostic
recalculée est dans tmp/Antibes-172950-arrivee-corrigee.bimaestro-mep.json.
Pour le site, réexporter le paquet web depuis le plugin corrigé puis le recharger :
les anciens paquets contiennent encore le résultat stagnant mémorisé.

Le code web est dans viewer-web ; ces modifications locales ne publient pas le site hébergé.
