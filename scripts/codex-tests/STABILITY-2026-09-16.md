# Création de familles : diagnostic et exécution par étapes

## Observations locales

- Le journal Revit 2023 `journal.3124.txt` contient une opération `AJ Autojoin Total` de 271,9 secondes et une consommation mémoire privée dépassant 33 Go pendant les variations/restaurations de `Logo_500mm`. Après une annulation utilisateur, d'autres variations repartent. Cela établit un calcul natif coûteux ; cela ne démontre pas que chaque gel a la même cause.
- Les diagnostics du 16 septembre signalent aussi des références de profil invalidées, des contours auto-intersectés, des arêtes inférieures à 1 mm, des barreaux sans solide mesurable et une épaisseur incorrecte après variation du caniveau.
- Les erreurs de contour sont déjà rejetées par la validation. Les contraintes de profils décoratifs fixes étaient inutilement créées ; les contrôles géométriques ne choisissaient pas explicitement les niveaux de détail.

## Changements

- La passerelle conserve une seule création en cours et avance dans `CreateSteps` via des callbacks Revit `Idling`. Elle rend la main entre préparation des sous-familles, pièces, réseaux, tests et restaurations. Aucune transaction ouverte ne traverse une pause.
- Le résultat n'est renvoyé qu'après la fin et le nettoyage. Une annulation, une révocation des permissions ou un changement de document arrête les étapes suivantes. Les créations partielles non enregistrées sont fermées ; le projet utilisateur n'est pas enregistré.
- Les appels synchrones du banc natif parcourent le même générateur sans attendre l'interface. Aucun accès Revit ne part dans un `Task.Run` et aucune boucle `DoEvents` n'est introduite.
- Protection coopérative : 5 minutes **par étape** (seuil relevé de 2 à 5 minutes le 17 septembre), augmentation de mémoire privée de plus de 2 Gio sur l'opération. L'annulation native utilise `ProgressChanged` uniquement lorsque Revit l'autorise. Un appel natif non annulable peut encore bloquer jusqu'à son retour. La durée totale n'est pas limitée à 5 minutes.
- Les profils fixes n'ont plus de guides par sommet. Les guides pilotés sont plus courts et leurs références sont relues après régénération. Budget de 96 sommets pilotés au total, avec le maximum existant de 24 par profil.
- Les mesures des barres cherchent une géométrie solide aux niveaux fin, moyen puis faible. Les dimensions constantes des barreaux sont associées à une formule, afin de rester constantes lors des variations du réseau.
- Les fichiers `%LOCALAPPDATA%\BIMaestro\Codex\Operations\*.json` enregistrent l'étape, l'état, la durée et la mémoire. Le panneau affiche le numéro d'étape.

## Vérification

- Tests de protocole, validation des descriptions et interface : réussis.
- Revit 2023, `polygon-profile.json` : validation native réussie ; exécution réelle via la passerelle en 58 étapes, transactions fermées entre étapes, arrêt demandé entre étapes réussi.
- Le scénario `diagnostic-caniveau.json` reprend les arguments du diagnostic réel, sans chargement ni placement dans un projet.
- Revit 2023, `diagnostic-caniveau.json` : validation native réussie, incluant les variations de profondeur qui produisaient une épaisseur de barreau incorrecte. Le temps total reste long ; ce test ne constitue pas une preuve de fluidité pendant chaque appel natif.

Le découpage conserve le travail dans le document temporaire pendant l'appel. Il ne constitue pas un point de reprise persistant après fermeture ou plantage de Revit. La nouvelle DLL doit être installée et Revit redémarré pour être utilisée dans une session existante.
