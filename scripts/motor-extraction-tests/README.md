# Prototype MEP : extraction moteur

Dans Revit, ouvrir une vue 3D orthographique et cadrer une pompe. Dans le menu déroulant **Beta > MEP Booster > Extraction moteur**, sélectionner un équipement mécanique. Le prototype ne reconnaît pas automatiquement le moteur : le pavé initial couvre l'équipement entier.

Définir les dimensions X/Y/Z et le centre du pavé en millimètres par rapport à l'origine de la famille. Choisir une direction locale, une course et une marge. Le pavé fantôme montre le volume nominal ; la marge est incluse dans le calcul des obstacles. Cliquer **Analyser tout le trajet**, puis déplacer le curseur ou lancer la lecture. Toute modification des réglages invalide le rapport précédent.

Le moteur reste virtuel : aucun élément, paramètre, raccordement ou surcharge graphique n'est écrit dans le projet. Le fantôme disparaît à la fermeture de la fenêtre. La fenêtre modale conserve le cadrage de départ ; fermer et rouvrir pour changer de vue.

## Périmètre

- Six directions locales, translation rectiligne d'un pavé orienté avec la famille, y compris les occurrences retournées.
- Volume balayé continu, puis recherche de la première pénétration avec une précision de course d'environ 1 mm. Le contact tangent sans volume d'intersection n'est pas signalé ; utiliser une marge positive.
- Solides du document complet et des liens Revit directs chargés, même hors visibilité de la vue. Transformations des liens appliquées. Les cadres rouges représentent les boîtes englobantes des obstacles, pas leurs surfaces exactes.
- La pompe et ses sous-composants sont exclus : le contrôle ne valide pas l'extraction interne du moteur hors du corps de pompe.
- Les géométries illisibles, sans solides, maillées et les liens non chargés rendent le résultat incomplet. Les liens imbriqués dans le périmètre sont signalés sans analyse. Aucune certification de maintenance, d'outillage, de manutention ou de procédure fabricant.
- Réglages temporaires, sans enregistrement de profils dans cette version.

## Vérification

`dotnet run --project scripts/motor-extraction-tests/MotorSweep.Tests.csproj`

Sept contrôles autonomes : trajet libre, recouvrement initial, obstacle final, obstacle mince intermédiaire dans les deux sens, marge et nombre borné d'évaluations. Ces contrôles valident la recherche de première collision, pas le noyau géométrique natif Revit.

À vérifier dans Revit avant diffusion : pavé sur une pompe tournée et retournée ; obstacle mince au milieu avec départ/arrivée libres ; collision initiale ; marge positive ; obstacle dans un lien tourné/translaté ; lien absent ; modification des champs après analyse ; animation, fermeture et absence d'écriture dans le modèle.
