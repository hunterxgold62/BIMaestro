# Apparence des propriétés

Bouton **BIMaestro → Couleur et information → Apparence propriétés**.

Sélectionner une vue ou un seul élément, ouvrir le bouton, puis choisir la portée (générale, catégorie ou nom de famille/type), le groupe ou le paramètre et les couleurs. Les variantes clair/sombre sont indépendantes ; la variante active suit le thème Revit. Actualiser la sélection permet de garder la fenêtre ouverte en changeant de contexte. Une sélection multiple reste native.

**Aperçu dans Revit** est temporaire ; fermer l'annule. **Enregistrer et appliquer** conserve les règles dans `%APPDATA%/BIMaestro/PropertiesAppearance.json`. Décocher l'activation et enregistrer restaure le dessin natif sans effacer les règles. Vider un profil ne retire pas les profils plus généraux. Un profil type utilise catégorie + famille/type (ou ViewType pour une vue), et s'applique donc aux mêmes noms dans les autres projets ; il ne cible pas une instance unique.

Le dessin réversible suit les identités et rectangles des vraies cellules natives. Aucun paramètre du modèle n'est écrit. Les champs en lecture seule, paramètres homonymes ambigus et éditeurs actifs gardent leur rendu Revit. La couleur du texte peut être corrigée pour maintenir un contraste de 4,5. Les réglages prioritaires sont ceux du type, puis de la catégorie, puis généraux ; dans une même portée, un paramètre prime sur son groupe. Une variante vide hérite du profil plus général.

## Validation du 20 octobre 2026

- Compilations complètes Revit 2023, 2024, 2025 et 2026 : réussies (avertissements existants).
- Revit 2025.5 : ouverture depuis le bouton réel du ruban ; aperçu de Graphismes (16 lignes reconnues) ; texte vert et fond vert pâle appliqués aux cellules ; champ grisé Valeur de l'échelle conservé ; défilement ; enregistrement, fermeture et réouverture du profil ; désactivation et retour au rendu natif.
- Les couleurs et leur persistance sont vérifiées visuellement en mode clair sur 2025. Les autres versions et le rendu natif sombre ne sont pas encore qualifiés visuellement pour ce nouveau bouton.
- Le module natif vérifie l'empreinte MFC, les exports Objective Grid et le RTTI avant de lire la grille. Un binaire non reconnu conserve les préférences sans appliquer le dessin. Ce garde-fou implique qu'une mise à jour Revit/MFC peut désactiver le rendu jusqu'à validation du nouveau binaire.

Les captures dans `evidence/` sont de vraies captures Revit. Le petit complément de validation temporaire n'est pas livré avec le plugin.

## Tests des préférences

`dotnet run --project scripts/properties-appearance-tests/Verify.csproj`

Ces tests lient le modèle de préférences de production et utilisent un fichier temporaire isolé. Ils couvrent la priorité des portées, l'héritage des variantes, le contraste, la fusion de sauvegardes entre sessions, le retrait d'un profil et la conservation d'un fichier corrompu.
