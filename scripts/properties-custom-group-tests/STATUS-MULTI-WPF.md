# État des essais — sélection multiple et repères

- r9 : compilation réussie ; capture multiple-group.json dans tmp/properties-custom-group-tests/2025-r9 : 46 lignes, header natif true, sélection de deux murs. Cela ne prouve pas encore une écriture sur les deux murs.
- r11 : WPF réel lié au laboratoire compilé ; commande editor exécutée sans erreur. Repères d'origine calculés avant recherche/repli/réorganisation, mêmes objets de ligne dans les deux colonnes. Vérification visuelle finale encore nécessaire.
- PropertiesAppearanceCommand.Open : raccordement opt-in au laboratoire uniquement si son OpenEditor confirme la fixture et le PID. Ce changement de source n'est PAS installé dans la DLL BIMaestro existante. Le bouton installé ne bénéficie donc pas encore de ce raccordement.
- Le laboratoire expose un champ de nom et Appliquer le groupe personnalisé, avec les six paramètres de fixture. La sélection libre des membres n'est pas implémentée.
- 15 tests du collecteur passent ; ils ne sont pas des tests UI WPF ni une qualification de l'écriture multiple.
- Pas de remplacement du plugin installé. Ne pas annoncer le raccordement du bouton installé comme terminé.
