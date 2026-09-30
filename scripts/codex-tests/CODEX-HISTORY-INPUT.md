# Reprise d'une grosse discussion Codex

Le journal local examiné le 28 septembre 2026 contenait 1 772 581 caractères,
pour un brouillon de 123 caractères. L'erreur `Input exceeds the maximum length
of 1048576 characters.` était suivie de `no rollout found for thread id ...`.

Le contexte de secours copiait le journal complet (y compris les résultats
techniques Revit) dans un seul message. Le changement du réglage Internet
invalidait auparavant la session et activait ce contexte. Le message était
construit avant `thread/start`, qui enregistrait un nouvel identifiant avant
le rejet de `turn/start`. Cet identifiant pouvait ensuite ne correspondre à
aucun rollout sauvegardé. La capture seule ne prouve pas quel réglage ou quelle
reprise a déclenché ce chemin chez l'utilisateur.

Le correctif conserve la session native Codex lors du changement Internet et
réapplique les réglages avec `thread/resume`. Sans session native, le contexte
reprend au maximum les 8 000 premiers et 40 000 derniers caractères du journal,
avec une indication explicite des omissions et une instruction d'inspection
de la famille actuelle. Ce sont des extraits, pas un résumé exhaustif. Les
anciennes pièces jointes ne sont pas retransmises. Le journal local complet,
le brouillon et les références RFA restent sauvegardés.

Seule l'erreur explicite `no rollout found for thread id ...` déclenche une
nouvelle session avec ce contexte limité. Les autres erreurs de reprise restent
visibles ; aucune opération Revit antérieure n'est rejouée par ce mécanisme.

Validation : `scripts/codex-tests/Run.ps1 -RealCodex`.

- Le vrai serveur Codex rejette 1 048 577 caractères avec le message exact ;
  aucun tour modèle accepté ni connexion de compte n'est demandé par ce test.
- Les tests de fenêtre utilisent les vrais clients et un serveur de protocole
  simulé qui applique la même limite. Un journal de plusieurs millions de
  caractères, avec échappements JSON et Unicode, se poursuit en un seul tour.
- Les cas couvrent absence de session, rollout introuvable, changement Internet
  avec session native et erreur de reprise indépendante. Le fichier d'historique
  reste intégral et les références RFA sont conservées.

La reprise avec un modèle connecté et la modification du RFA dans Revit restent
à vérifier par l'utilisateur après installation ; les tests ne recréent pas sa
famille et ne modifient pas sa discussion locale.
