# Bibliothèque communautaire Famille IA

## État au 20 septembre 2026

- Service déployé : https://bimaestro-family-library.bimaestro-community.workers.dev
- R2 `bimaestro-families`, Standard, WEUR, accès public direct désactivé.
- D1 `bimaestro-family-catalog` : `1918916c-c5cb-4165-8db2-0532291d859c`.
- Workers Free confirmé par capture utilisateur ; aucun abonnement ajouté.
- Aucun dialogue automatique après création : partage manuel depuis Famille IA ou le bouton indépendant « Bibliothèque commune ».
- Tout RFA compatible peut être proposé, avec choix personnelle/IA et inspection de sa version enregistrée. Interface WPF avec catégories, cartes, fiche et « Mes publications ».
- SHA-256 vérifié avant chargement, cache local réutilisé, 20 000 000 octets maximum par fichier, aucun retry automatique.
- Première version textuelle : miniatures PNG non partagées.
- Compatibilité : un client Revit 2025 voit les familles 2023, 2024 et 2025 ; les versions ultérieures sont exclues. Le paramètre API `revitVersion` est un maximum inclusif, avec minimum 2023.
- Chaque fiche expose `downloadCount`, affiché dans la fenêtre WPF. Incrément atomique lors de la remise d'un fichier par le serveur ; ce n'est ni un nombre d'utilisateurs uniques ni une confirmation de transfert entièrement reçu. Le cache local ne l'incrémente pas. Les compteurs commencent à zéro à l'installation de la migration ; les anciens téléchargements ne sont pas reconstitués.
- Compilation Debug et Release2024 réussie. Interface native Revit non exercée pendant cette session.

## Propriété et retrait

Identité locale aléatoire de 256 bits par endpoint, protégée par DPAPI dans le profil Windows et conservée avant publication. Seul son SHA-256 est stocké côté serveur, jamais exposé au catalogue. La perte de cette identité ou un changement d'ordinateur empêche de retrouver automatiquement les droits de retrait.

`X-Owner-Token` identifie le propriétaire ; `X-Family-Origin` vaut `personal` ou `ai`. Le catalogue expose `origin` et `isOwner`, avec filtres `origin` et `mine=1`. Un doublon ne transfère jamais la propriété. Les anciennes publications restent sans propriétaire, d'origine inconnue ; leur retrait nécessite une intervention administrative.

`DELETE /v1/families/{id}` retire uniquement une publication possédée par cette identité : fiche masquée et nouveaux téléchargements bloqués. Les copies déjà téléchargées et transferts commencés restent disponibles. Le fichier reste privé dans R2, sans remboursement des compteurs. Le retrait reste soumis aux quotas.

Migration déployée : `migrations/0003_family_ownership.sql`, à appliquer une seule fois avant le nouveau Worker. 19 tests Node/SQLite passent, couvrant aussi propriété, doublons, retrait et recherche avec origine vide. Test réel `live-test.mjs ownership` : publication 201, retrait tiers refusé 403, retrait propriétaire 204, téléchargement ultérieur 404. Ce test utilise une fixture native distincte et n'est pas répétable avec le même fichier après retrait.

## Garde-fous

Budget demandé : **0 EUR**. Refuser une opération avant de dépasser **80 %** du quota gratuit. Une indisponibilité est préférable à un dépassement.

`quota.js` réserve atomiquement les coûts dans D1 avant tout accès R2. Les envois simultanés et en cours sont comptés. Les réservations restent comptées après un échec, un doublon ou un crash. Les plafonds sont également bornés par les constantes du code.

| Compteur | Plafond à 80 % |
|---|---:|
| R2 fichiers | 8 000 000 000 octets |
| R2 classe A | 800 000 opérations |
| R2 classe B | 8 000 000 opérations |
| Requêtes métier admises | 80 000 |
| Budget conservateur D1 lectures | 4 000 000 lignes |
| Budget conservateur D1 écritures | 80 000 lignes |
| Budget conservateur D1 de cette base | 400 000 000 octets |

**Tous les compteurs sont cumulatifs, sans remise à zéro automatique**, même pour les quotas fournisseur quotidiens ou mensuels. Cette version s'arrête donc plus tôt : 256 lectures et 16 écritures D1 sont réservées par requête, soit au plus environ 5 000 opérations métier avant rapprochement manuel. Le stockage D1 réserve 16 Ko par tentative de dépôt et 64 Ko initiaux. Ces budgets conservateurs ne sont pas les métriques de facturation réelles.

Les sources effectives sont `quota_policy` et `quota_state`. `library_configuration.budget_policy` est un historique descriptif. L'ouverture nécessite `LIBRARY_ENABLED=true`, `enabled=1` et une vérification non expirée. Un seul bucket et une seule base étaient présents à la vérification initiale.

La vérification expire le **20 octobre 2026 à 00:00 UTC** : le service se fermera sans renouvellement après contrôle du compte et des tarifs. Aucun moniteur récurrent n'est installé. Ne jamais remettre les compteurs à zéro ou reporter l'échéance sans rapprocher l'usage réel de tout le compte.

## Tests

14 tests Node/SQLite réussis : chaque compteur à 1 % et 80 %, huit connexions SQLite concurrentes, absence d'accès R2 après refus, flux trop grand/tronqué ou bloqué (délai de 90 secondes), politique invalide/expirée, crash, doublons, compatibilité des versions et comptage des téléchargements concurrents. Test réel supplémentaire : famille 2024 visible avec le filtre 2025, compteur passé de 0 à 1 après téléchargement vérifié.

Test réel Cloudflare :
1. Seuil temporaire 1 %, ajout d'une réservation **fictive** de 100 000 000 octets.
2. Dépôt tenté de 446 464 octets : **HTTP 429 quota_exceeded**, zéro famille ajoutée, zéro écriture R2 réservée.
3. Retrait du seul delta fictif, préservation des autres compteurs, retour à 80 %.
4. Dépôt réel de la fixture native Revit 2024 « BIMaestro - Famille de test » : **HTTP 201**.
5. Catalogue puis téléchargement HTTP 200, octets identiques et SHA-256 vérifié.

SHA-256 : `cea870d747ea2d132773c816355891a119e0413dd40e810c0cfba05f318834ae`.
Volume réellement stocké : **446 464 octets**, une écriture et une lecture R2 pour cet aller-retour. Les 100 Mo n'ont jamais été téléversés.

Commande locale : `node --test cloudflare/family-library/test/*.test.js` (Node 22.18+, SQLite intégré).

## Exploitation

Le schéma est additif et initialise toute nouvelle installation fermée. Le fichier wrangler reste fermé par défaut pour prévenir une ouverture involontaire. `build-api-deployment.mjs --enabled` produit une requête MCP Cloudflare multipart sans credentials ; il n'envoie rien lui-même. Le verrou D1 est indépendant.

Installation existante : vérifier `PRAGMA table_info(shared_families)` puis appliquer `migrations/0002-download-count.sql` une seule fois si la colonne manque, avant le déploiement du Worker. Le schéma neuf contient déjà cette colonne.

Fermeture immédiate : `UPDATE quota_policy SET enabled=0 WHERE id=1;`. Pour couper aussi les lectures D1, redéployer avec `LIBRARY_ENABLED=false` ou désactiver workers.dev.

`live-test.mjs blocked` attend un refus ; `live-test.mjs roundtrip` transmet uniquement la fixture native dont le chemin est fixé, la recherche puis contrôle son téléchargement. Ces commandes font de vrais accès réseau. Le test blocked ne prépare pas le faux compteur : effectuer cette préparation en fenêtre contrôlée et retirer uniquement le delta fictif ensuite.

## Limites de la garantie

- Aucun plafond global natif Cloudflare à 0 EUR n'a été vérifié. Les alertes de budget ne coupent pas la facturation.
- Les requêtes refusées consomment encore Workers et parfois D1. Le code déjà invoqué ne peut empêcher leur arrivée à 80 %. Workers/D1 doivent rester sur Free pour leur arrêt natif.
- Autres applications, accès d'administration, changements manuels et tarifs échappent aux compteurs de la bibliothèque. Toute nouvelle ressource nécessite un réexamen des quotas partagés ; ces garde-fous ne garantissent pas la facture entière du compte.
- Le serveur vérifie taille, signature du conteneur et intégrité, mais ne lance pas Revit pour vérifier la géométrie. Aucun statut « validé » n'est attribué automatiquement.
- Les lignes pending après échec restent conservées pour investigation, sans réécriture automatique.

Sources : [R2](https://developers.cloudflare.com/r2/pricing/), [Workers](https://developers.cloudflare.com/workers/platform/limits/), [D1 tarifs](https://developers.cloudflare.com/d1/platform/pricing/), [D1 limites](https://developers.cloudflare.com/d1/platform/limits/), [facturation](https://developers.cloudflare.com/billing/understand/usage-based-billing/).
