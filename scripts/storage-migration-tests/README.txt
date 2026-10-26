Migration automatique des données utilisateur — prochaine mise à jour

Destination : Documents\BIMaestro_Revit, dans le dossier Documents renvoyé par Windows
(y compris les Documents redirigés vers OneDrive).

Nouveaux utilisateurs : création directe du nouveau dossier.
Anciens utilisateurs : copie de Documents\RevitLogs dans un dossier de préparation
unique, comparaison SHA-256 de chaque fichier avec la source, adaptation des chemins
absolus internes dans les préférences JSON/TXT, puis publication du nouveau dossier.
Les informations de licence et l'identifiant d'installation sont conservés.
Le dossier RevitLogs original reste présent et n'est jamais supprimé automatiquement.
Les chemins externes et les dossiers portant des noms ressemblants ne sont pas modifiés.

Un marqueur .bimaestro-migration-v1 dans la destination empêche toute recopie au
redémarrage : les nouvelles données de BIMaestro_Revit deviennent la référence.
Un mutex par emplacement Documents sérialise les migrations simultanées.
Fermer les autres sessions Revit avant le premier démarrage de la mise à jour.
Après migration, éviter de réutiliser une ancienne version du plugin : celle-ci
continuerait à écrire dans RevitLogs, dont les changements ne sont pas fusionnés.

Si la destination existe déjà sans marqueur, si un fichier est verrouillé, si une
copie est incomplète ou si un lien/jonction est détecté, aucun écrasement n'est fait.
Le plugin utilise RevitLogs pendant cette session et affiche un avertissement.
La migration sera retentée au démarrage suivant. Les éventuelles copies de
préparation incomplètes restent sur place pour contrôle, au chemin de l'avertissement.
Les fichiers OneDrive à la demande sont lus normalement ; leur téléchargement doit
être possible. Leur indisponibilité déclenche le même repli sans perte de la source.
La copie demande de l'espace libre et peut allonger le premier démarrage.

Cas historique de Sélection d'objet unique : si sa configuration se trouvait dans
les Documents du OneDrive préféré, distincts des Documents Windows, ce chemin exact
est également résolu/migré en conservant la priorité historique. Aucune recherche
dans les autres dossiers ou profils Windows n'est effectuée.

Retour arrière : fermer Revit. RevitLogs contient l'état d'avant migration ; il ne
contient pas les nouvelles écritures de BIMaestro_Revit. Sauvegarder les deux dossiers
avant toute intervention et réconcilier les changements avant d'utiliser une ancienne
version. Ne pas supprimer le marqueur pour forcer une fusion : aucune fusion n'est prévue.

Tests isolés (aucun accès aux vrais Documents) :
  dotnet run --project scripts/storage-migration-tests/StorageMigration.Tests.csproj --framework net8.0
  dotnet run --project scripts/storage-migration-tests/StorageMigration.Tests.csproj --framework net48

Les fixtures sont conservées sous tmp/BIMaestro-storage-tests-<identifiant>.
Couverture : installation neuve, données binaires, licence, dossiers vides,
préférences et chemins similaires, conservation de la source, relancement,
destination existante, fichier verrouillé et reprise, migrations concurrentes,
absence d'impact sur les dossiers non concernés.

Avant diffusion : essai dans Revit sur un profil de test avec copie des données
réelles, notamment les préférences de familles et les fichiers OneDrive hors ligne.
Les tests automatisés ne reproduisent pas le service OneDrive ni les anciennes
versions du plugin en fonctionnement.
