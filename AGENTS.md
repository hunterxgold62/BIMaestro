# Consignes de sécurité demandées par l’utilisateur

- Ne pas lancer le PowerShell intégré au runtime Codex (`WindowsApps/OpenAI.CodexPrimaryRuntime.*/dependencies/native/powershell/pwsh.exe`). Cortex XDR a bloqué une invocation le 19 novembre 2026.
- Ne pas utiliser les commandes shell de Codex qui ajoutent automatiquement le préambule `[Console]::OutputEncoding=[System.Text.Encoding]::UTF8`, ni relancer cette invocation par un autre moyen.
- Pour lire les fichiers et lancer les compilateurs, utiliser les API de fichiers et les processus directs sans shell (`shell: false`, `windowsHide: true`), par exemple via Node, lorsque ces opérations sont autorisées.
- Ne pas désactiver Cortex, créer d’exclusion, modifier ses règles, masquer ses alertes ou contourner un blocage. Si un outil direct est également bloqué, arrêter cette voie et signaler le blocage.
