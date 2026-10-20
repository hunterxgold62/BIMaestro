# Corps de la palette : troisième série

20 octobre 2026, branche `codex/properties-palette`. Nouvelle série exécutée dans Revit 2025.5 réel, thème clair, sur PropertiesFixture.rvt. [Comparatif visuel](TextGallery.html).

## Résultats

- **Zoom natif** : deux `WM_MOUSEWHEEL` de +120 avec `MK_CONTROL` transmis à chaque GXWND de la palette. Le texte, les champs et les hauteurs grandissent réellement. Deux messages inverses ont ramené le compteur des requêtes à zéro et le rendu à sa taille initiale. C'est une commande d'interface native, pas une API publique Revit de zoom de palette. Autodesk documente le geste Ctrl+molette dans la palette.
- **Neon** : texte magenta à gauche et vert à droite, par recoloration des pixels sombres. Le seuil spatial ne reconnaît pas les paramètres et peut couper un libellé long. Les contrôles peuvent aussi être colorés.
- **Blueprint** : transformation de luminance du fond clair vers bleu nuit/turquoise, sur tout le corps de grille. Ce n'est pas un thème sombre sémantique ; l'éditeur natif peut redessiner son apparence.
- **Zebra** : fonds blancs alternés toutes les 34 px. Le décalage par rapport aux véritables lignes est visible ; rejeter cette version comme fonction finale sans synchronisation fiable.
- **Spotlight** : région claire autour du pointeur sur 96 px de hauteur, reste assombri ; repli centré hors grille. Mouvement vérifié et infobulle native conservée. Aucun suivi sémantique de ligne.
- **Bold** : dilation horizontale d'un pixel des caractères verts, qui peut aussi toucher des traits ; pas une attribution de police native.

Les effets graphiques conservent les coordonnées natives et ne déplacent pas les cibles. Ils ne constituent pas une qualification des éditeurs, sélections, clavier, accessibilité, modes sombres et contextes MEP. Aucun contenu de paramètre n'a été volontairement changé.

La recherche des exports de og1100asu.dll 2024 a confirmé des méthodes C++ SetZoom, SetRowHeight, SetStyleRange et CGXStyle::SetFont/SetTextColor. Cela ne constitue pas un pont exploitable : aucun objet CGXGridCore valide récupéré proprement à partir du HWND. Aucun pointeur ou offset C++ inventé, aucun appel de méthode C++ sur un objet supposé.

## Vérification

Empreinte avant et après les essais : `965F318E2728E54CF63E35F0674B6B39093384AF6788B7338EF4A011DFC92FD5`, portant sur la vue et le mur jetable. Les rapports sont dans `evidence/text-round3/`. Les métriques sont cumulatives entre modes : ne pas attribuer le total à un seul effet. 106 dessins, moyenne 3,905 ms, maximum 13,082 ms dans le rapport de restauration de cette séquence, sans garantie de fluidité universelle.

Effets restaurés avec la DLL qui les a installés : zéro hook / zéro propriété WPF sauvegardée. Zoom annulé avec la première DLL qui avait demandé les deux crans. Manifeste temporaire retiré. Aucune modification de BIMaestro de production.

## Reproduction laboratoire

Source : `versions/2025/Advanced2025.cs`. Compiler via `versions/2025/Build.ps1 -Name advanced2025-effects-v1`. Harnais commun décrit dans README. Nouveaux modes : `zoom-in`, `zoom-out`, `split`, `neon`, `bold`, `blueprint`, `zebra`, `spotlight`. **Le mode restore restaure les effets graphiques, pas les requêtes de zoom : inverser exactement les crans de zoom avant de terminer.** Ne pas dépasser les limites du zoom et prétendre ensuite qu'un nombre inverse suffit sans contrôle visuel. Aucun bouton permanent installé.

Sources primaires :

- [Autodesk : palette Propriétés et Ctrl+molette](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-GetStarted/files/GUID-A764EA7A-FE26-469B-857C-F3A70812FC34.htm)
- [Perforce : styles de cellules Objective Grid](https://help.perforce.com/stingray/2025.1/stingray_doc/Content/Stingray_Studio_HTML_User_Guide/ogugTOC/ogug-designoverview.htm)
- [Perforce : SetStyleRange](https://help.perforce.com/stingray/2024.1/Stingray_Studio_API_Documentation/Content/Grid/CGXGridCore_0jhi.htm)
