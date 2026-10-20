# Essais Revit 2024 — seconde exploration

DLL de travail : `tmp/properties-tests/2024-experiment/palette2024round2c.dll`.
Commandes : inspect, compact, large, dark, textonly, style, font, restore.
Restaurer la même DLL entre chaque apparence. La première DLL b a été restaurée avant de charger c.

## Observé en exécution

- Palette native : WPF `UIFramework.TypeSelectorToolBarControl` accessible même flottant. Deux titres WPF, sélecteur natif, image de prévisualisation, boutons et textes.
- Compact : 47 propriétés de dépendance enregistrées, police11, aperçu32px, padding1. Restauration DP0 vérifiée.
- Sombre : fond #1B2722, texte #E1F0E7, police14, aperçu48, bordure verte. 47 propriétés enregistrées. Ce thème ne prétend pas recolorer les cellules natives.
- Police de grille : deux HWND GXWND obtenus par EnumWindows limité au PID Revit puis parcours des enfants. WM_GETFONT=0 avant et après WM_SETFONT avec Segoe UI -24. L’essai ne démontre aucun changement de densité de la grille. HFONT sauvegardés/rétablis, police temporaire supprimée par DeleteObject.
- Empreinte de paramètres vue et murs : 89 entrées, SHA256 `1AB9126BB534D9A2ECFAC3AAEB6A860D19B26838E6C31A1A35E1DB50B8A8F8FA`, inchangée sur inspect, compact, restore, font, restore et dark.

Les rapports exacts sont dans `tmp/properties-tests/2024-round2/*-report.txt`. Les captures sont réalisées séparément par l’agent principal.

## Limites

Aucun accès aux cellules via pointeur C++, aucun message WM_USER, aucune modification des paramètres du projet. L’aspect visuel de la grille reste le contrôle Revit natif. Les changements WPF et hooks sont éphémères, sans bouton ni activation de production. Les variantes large et textonly nécessitent leurs essais/captures avant de les présenter comme validées.

Large exécuté : 47 propriétés sauvegardées, police19/aperçu88 ; empreinte89 identique. Captures root en cours. Le mode sombre a été restauré avant cet essai.

Textonly exécuté : aperçu masqué, 48 propriétés sauvegardées. Style flottant exécuté : deux hooks de contour GXWND obtenus grâce au parcours de toutes les fenêtres du PID. Restauration finale : zéro hook, zéro propriété sauvegardée, même empreinte89. Les cadres et la variante sans aperçu restent à évaluer visuellement sur une capture isolée. Toutes les variantes ont conservé les valeurs du projet.
