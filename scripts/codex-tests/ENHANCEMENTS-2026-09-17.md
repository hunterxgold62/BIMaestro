# Familles : améliorations du 17 septembre 2026

- `component_grids` répète un module complet imbriqué suivant deux axes distincts. Chaque module contient des blocs rectangulaires avec matériaux et niveaux de détail. Les dimensions du module restent fixes ; les étendues commandent les nombres de colonnes et de rangées. Limites : 4 grilles, 20 composants par module, 200 modules visibles et budget global de 750 solides. Ce mécanisme n'importe pas un assemblage RFA arbitraire.
- Les angles acceptent 0 à 180 degrés. La construction initiale à 37 degrés évite les contraintes orthogonales implicites avant d'appliquer la valeur demandée.
- Les trois permissions de contexte sont cochées à l'ouverture du panneau. Leur révocation reste effective ; une nouvelle discussion conserve la remise à zéro du mode direct.
- `revit_family_template_info` mesure les murs et sols du gabarit et expose les paramètres existants. Les instructions demandent cette inspection avant création. Le placement sur une face mesurée ne constitue pas une liaison automatique à la face d'un mur d'épaisseur différente : utiliser un hébergement par face pour les appliques concernées.
- Les échecs de chargement et d'ouverture conservent davantage de contexte : fichier, exception, document actif, version Revit et journal. Une famille déjà ouverte en arrière-plan peut être fermée puis activée si elle n'a aucune modification non enregistrée. L'ancienne exception interne d'ouverture ne permet pas d'établir sa cause exacte.

## Vérification

- Tests de descriptions, protocole et interface : réussis avec `Run.ps1`.
- Compilation Release de la solution : réussie.
- Revit 2023 : angles initiaux à 0 degrés, variations à 90 et 180 degrés réussies sur X, Y et Z.
- Revit 2023 : grille X/Z constituée d'un cadre, vitrage et surface photovoltaïque, avec représentations faible/moyenne/fine. Test nominal à 9 colonnes et 2 rangées ; variations vers zéro et une colonne/rangée et restauration réussies. Dimensions des panneaux illustratives, sans référence fabricant revendiquée.
- Mur du gabarit testé : épaisseur 150 mm, faces Y = -75 et +75 mm.
- Résultats locaux : `tmp/codex-native-validation/2023-enhancements-1/result.json` pour les angles et `tmp/codex-native-validation/2023-grid-2/result.json` pour la grille après correction du conflit de noms de matériaux.

Les tests natifs utilisent des familles temporaires, sans modification du projet utilisateur ni enregistrement des familles de test. La DLL compilée n'est pas encore déployée dans l'installation utilisateur.
