# Palette Propriétés native : essais 2023 à 2026

Branche `codex/properties-palette`. Bilan du 20 octobre 2026. Quatre instances réelles de Revit ont été utilisées, avec un projet jetable par version ; les sondes ont été restaurées et leurs manifestes temporaires retirés. Aucun bouton permanent ni changement de l’installation BIMaestro de production.

[Ouvrir le comparatif des captures](Gallery.html).

## Résultats réellement obtenus

| Essai | Version exécutée | Résultat |
| --- | --- | --- |
| Sélecteur vert et contours de grille | 2023 | 24 propriétés WPF et 2 contours ; restauration confirmée. |
| Texte du sélecteur agrandi ou compact | 2023 | Police 16, ou police 11 avec padding 2. Cela ne change pas la police des cellules. |
| Sélecteur compact, grand, sombre, sans aperçu | 2024 | Police 11/aperçu 32, police 19/aperçu 88, contraste sombre, suppression de l’aperçu ; 47–48 propriétés restaurables. |
| Contour d’une palette flottante | 2024 | Deux GXWND trouvés en parcourant les fenêtres du processus ; corrige la limite de la première sonde. |
| Police des cellules via WM_SETFONT | 2024 | Message essayé sur deux grilles, sans changement de police démontré. |
| Bandeaux gris teintés vert ou bleu | 2025 | Surcouche graphique réversible après le dessin natif. Reconnaissance visuelle heuristique, pas identification des groupes par API. |
| Couleur des caractères dans la grille | 2025, thème clair | Prototype de recoloration des pixels de texte gris, avec maintien des éditeurs natifs. Les traits gris et cases peuvent aussi être recolorés ; aucune réécriture des libellés. |
| Habillage associé au thème sombre natif | 2025 | Activation puis restauration du thème natif ; aucun remplacement de palette. |
| Recherche intégrée au sélecteur | 2026 | Petit bouton ouvrant une recherche en lecture seule des données API ; vue ou élément unique, sans filtrage des véritables lignes. |
| Rafraîchissement de recherche et accents | 2026 | Relecture à chaque ouverture par ExternalEvent, contexte affiché, recherche sans accents, liste déroulante des résultats. |

Les années indiquent la couverture des essais, pas une exclusivité commerciale de chaque possibilité. Les prototypes n’ont pas tous été portés et validés sur les quatre versions.

## Vérifications et limites

Empreintes identiques pendant les séquences appliquer/restaurer dans les quatre versions, portant sur la vue active et le mur du projet jetable. Ce n’est pas une preuve exhaustive pour toutes les familles, paramètres, sélections multiples ou catégories MEP.

La grille est un contrôle natif MFC Objective Grid (GXWND/GXEDIT), avec un sélecteur WPF. Aucun pont managé vers les objets C++ de ses cellules n’a été trouvé. MSAA ne fournit pas les noms et rectangles de toutes les lignes. La recoloration graphique ne fournit donc pas un moyen fiable de déplacer, masquer ou traiter sémantiquement une propriété particulière.

Les essais de bandeaux optimisés mesurent environ 0,3–4 ms par dessin sur cette petite fixture. La recoloration complète des caractères a mesuré environ 3,8 ms de moyenne, avec une pointe proche de 21 ms dans la séquence enregistrée. Elle reste expérimentale et ne doit pas être distribuée telle quelle comme garantie de fluidité.

La recherche 2026 expose les valeurs API en lecture seule. Elle ne rejoint ni ne masque les lignes natives et ne remplace pas leurs éditeurs. Les paramètres de type, les favoris et les règles avancées de sélection restent des extensions à étudier. Une sélection multiple est signalée explicitement, sans afficher silencieusement les données de la vue.

## Sources et reproduction

- [2023 : code et protocole](versions/2023/README.md)
- [2024 : code et protocole](versions/2024/README.md)
- [2025 : code et protocole](versions/2025/README.md)
- [2026 : code et protocole](versions/2026/README.md)
- [Cadre de l’API publique et sources primaires](API-NOTES.md)
- [Preuves de la deuxième série](evidence/round2/results.json)

Le harnais commun se compile avec `Build.ps1 -RevitVersion ANNEE -OutputTag '-round2'`. Les extensions se compilent dans chaque dossier `versions/ANNEE`. Dans une instance de laboratoire uniquement, `experiment.txt` choisit le mode et `control.txt` contient `run CHEMIN_ABSOLU_DLL`. Restaurer avec **la même DLL**, qui conserve les anciennes propriétés et les ressources natives. Le harnais ne doit pas devenir une interface distribuée de chargement arbitraire de DLL.

Les versions 2025/2026 installées ici utilisent des correctifs .NET 10 ; les chemins des compilateurs et références sont adaptés à ce poste. Les scripts ne constituent pas une configuration SDK universelle.

Les captures sont de vraies fenêtres Revit, sans génération ni retouche. Le comparatif affiche parfois une zone cadrée par CSS, avec accès au fichier complet. La première série et ses rapports restent dans `evidence/` ; la deuxième est sous `evidence/round2/`.
