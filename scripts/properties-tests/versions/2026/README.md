# Recherche dans la palette native Revit 2026

DLL finale : `tmp/properties-tests/2026-round2/experiment2026-r5.dll`. L’instance réelle 2026.5 a été testée sur une vue et un mur.

Le RootVisual `TypeSelectorToolBarControl` n’a pas de parent WPF modifiable. Le prototype enveloppe donc ce sélecteur original dans un Grid, ajoute un petit bouton de recherche et une Popup. Le contrôle Revit d’origine et les éditeurs de la grille restent présents. `restore` restitue le RootVisual exact, retire le bouton et ferme la Popup.

La recherche porte sur les données API, en lecture seule. Elle n’agit pas sur les véritables lignes de la grille. Le contexte est indiqué (vue ou élément unique). La sélection multiple est signalée explicitement. Les résultats ignorent les accents et ne sont plus limités à trois lignes : un défilement est disponible.

La première version nécessitait une actualisation manuelle ; un test réel a montré des données de mur après retour à la vue. R5 corrige cela : chaque ouverture du bouton déclenche un ExternalEvent qui relit la sélection en contexte API autorisé. Le thème du popup suit le thème natif lors du chargement ; les modes manuels clair/sombre restent disponibles pour les essais.

Résultats : vue avec 60 propriétés, recherche `echelle` retournant deux valeurs liées au 1:100 ; mur avec 36 propriétés, `longueur` retournant 6096. Passage vue → mur vérifié par ouverture du bouton, sans commande de rafraîchissement. Empreinte de 96 paramètres identique : `31B1259452DFDB85CA324C8440E0A10E88B3C29D52D7727B3E00C05625C860B0`.

Modes : `inspect`, `add`, `query TEXTE`, `context`, `show`, `hide`, `light`, `dark`, `restore`. Après `add`, le bouton s’utilise dans la palette. Compilation : `Build.ps1 -Name experiment2026-r5`. Le choix manuel de thème est réservé aux tests ; il ne change pas le thème natif Revit.

Restauration finale confirmée : `Extension=False`, valeurs inchangées, événement externe disposé et session jetable fermée. Les paramètres de type, la navigation native, les favoris et une interface de production ne sont pas implémentés.
