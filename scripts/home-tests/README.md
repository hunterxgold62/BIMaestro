# Revit Home exploration

## Compact second series — 2026-10-16

User feedback retained the simple modern green direction and rejected dominant 2025/2026 previews. New variants are isolated in `home-concepts-legacy-v2.js` and `home-concepts-modern-v2.js`; the first series is preserved. Apply through `Set-Concept.ps1 -RevitVersion 2026 -Generation 2` (same restore option).

| Version | Mode retained | Second concept | Measured main image |
| --- | --- | --- | --- |
| 2023 | Classic `index.html` | Essentiel: compact green/cream cards, restrained heading and count chips | 76 px preview region |
| 2024 | Classic `index.html` | Registre: light slate blue, two wide columns on desktop | 92 px preview region |
| 2025 | New `udabrowser.html`, toggle enabled | Horizontal olive/ivory cards with file information first | 80 px image (previous 178 px) |
| 2026 | New `udabrowser.html`, toggle enabled | BIMaestro file index with aligned name/date/type/location columns | 52 px image (previous 142 px) |

Real screenshots and native evidence: `tmp/home-tests/concepts-v2/`. All four report nonempty native file collections (10, 14, 25, 10 respectively) and no horizontal overflow. Modern geometry reports no clipped titles, metadata outside cards or intersecting title/metadata rectangles. These checks establish current-session layout, not broad production lifecycle acceptance. One 2024 instance exited before capture; a fresh instance was launched and the final two-column concept captured successfully. No model or Autodesk installation asset changed. Preview windows remain open, with temporary registrations removed after startup.

## Four designed previews — 2026-10-16

Four separate concepts run inside the original Autodesk Home. Sources: `home-concepts-legacy.js` (2023/2024), `home-concepts-modern.js` (2025/2026). Apply with `Set-Concept.ps1 -RevitVersion 2026`; revert with the same command plus `-Restore`. These are session previews through the isolated native probe, not production startup integration.

| Version / concept | Design and practical change | Native screenshot |
| --- | --- | --- |
| 2023 / Atelier | Warm light palette; compact horizontal cards; real file size/date and folder breadcrumbs; live name/path search and project/family/sample filters | `tmp/home-tests/concepts/2023-atelier.png` |
| 2024 / Observatoire | Navy/cyan overview; large previews; project/family counts; real metadata; name/path search and sample visibility | `tmp/home-tests/concepts/2024-observatoire.png` |
| 2025 / Bibliothèque | Dark slate navigation, ivory gallery, larger cards; RVT/RFA accents; readable folder labels and native full-path tooltips | `tmp/home-tests/concepts/2025-bibliotheque.png` |
| 2026 / BIMaestro | Actual `Themes/BIMaestroTheme.xaml` palette: forest `#0F5132`, background `#F6F8FB`, white surfaces, `#E6EAF0` borders, 14px corners; compact heading and live filters | `tmp/home-tests/concepts/2026-bimaestro.png` |

Live checks: 2023 search `Le Mans` isolates one real project; 2024 family filter displays four files; 2025 family filter displays six files; 2026 project filter displays six files. Modern geometry checks report no horizontal overflow, clipped titles, title/metadata collision or metadata outside cards. Legacy inspect reports no horizontal page overflow. Preview counts are derived from actual recent-file cards, not model analysis. No model opened or changed.

The four preview windows are intentionally left open for comparison. Probe registrations are removed after startup, so loading these previews again requires the isolated harness; closing Revit removes the session changes. Existing production BIMaestro and Autodesk installed assets are unchanged. Search/list/other Home routes still need broader lifecycle acceptance before product integration.

Scope: modify the Autodesk Home page in place. No replacement Home window, project-browser modifications, model transactions, or Autodesk installation-file edits are part of this experiment.

## Installed-file inventory (2026-10-16)

`inventory.json` records local versions, asset paths, SHA-256 hashes, runtime configuration and live evidence references. `tmp/home-tests/matrix-results.json` preserves exact browser responses: **20/20 cases passed**, four presets plus restoration on all four installed versions.

| Revit | Installed file version | Browser evidence in Home assembly | Live result |
| --- | --- | --- | --- |
| 2023 | 23.0.11.19 | Live CEF, legacy `index.html` | 5/5 verified |
| 2024 | 24.3.30.11 | Live CEF, legacy `index.html` | 5/5 verified |
| 2025 | 25.5.0.57 | Live CEF, new `udabrowser.html` | 5/5 verified |
| 2026 | 26.5.0.55 | Live WebView2, new `udabrowser.html` | 5/5 verified |

All four installations contain `AddIns/ModelBrowser/resource/index.html` with a `root` element, `static/css/index.main.css` and `static/js/index.bundle.js` (title: Cloud First Model Browser). Their existence does not prove that the visible **new Home** loads these assets. Capture the actual browser URL, title, root and component type before applying styles. Test new and legacy Home separately where the installed version offers that switch. Do not change this preference silently.

The 2026 ModelBrowser manifest specifies `UseRevitContext=False` and `ContextName=ModelBrowser`; reflection must account for a separate assembly load context. Its installation also contains `web-resources/LCasUI/2026.2`, which is not by itself proof of Home ownership.

## Runtime/build findings

- SDK installed: 9.0.300. WindowsDesktop reference packs: 8.0.16 and 9.0.5. .NET Framework reference assemblies include 4.8.
- Installed WindowsDesktop runtimes: 8.0.16, 8.0.21, 9.0.5 and 10.0.10.
- The installed 2025 and 2026 `RevitAPI.runtimeconfig.json` files explicitly request `net10.0` / framework 10.0.0. Do not assume these patched installations still use the original major-version runtime. The .NET 10 runtime is present but a .NET 10 SDK/reference pack was not found in the inspected standard directories.
- Existing `scripts/codex-tests/StartNativeHarness.ps1` registers a temporary per-user add-in manifest, starts a dedicated Revit process, waits for a started marker, and removes that temporary manifest in `finally`. Its current ValidateSet supports 2023–2025; its build partner supports only 2023–2024. Reuse the lifecycle pattern, not the unrelated native test payload.

## Live acceptance matrix

The live matrix verified `compact`, `dark`, `blueprint`, `large` and `restore`. Each applied preset returned positive Home-card evidence and no detected title/date overlaps; restoration returned no injected stylesheet and no preset marker. Root-agent visual QA additionally checked these captured real Revit windows:

- `tmp/home-tests/screenshots/2023-compact.png`
- `tmp/home-tests/screenshots/2024-blueprint.png`
- `tmp/home-tests/screenshots/2025-dark.png`
- `tmp/home-tests/screenshots/2026-large.png`

Native Open → Cancel was exercised on all four versions. On 2025, list/grid switching and search for `BoosterFixture` were exercised; search returned two matching cards. Additional 2025 experiments reordered and colored the native cards, added alphabetical groups and full-path tooltips: see `2025-grouped.png` and `2025-search.png` in the same screenshot directory.

Final restoration is verified: all four matrix restore results report `styles=false` and `preset=null`. `tmp/home-tests/enhancements-restored.json` reports no enhancement mode, zero changed nodes, no replacement titles and no ordering overrides. `2025-restored.png` was visually checked against the original appearance. `native/Cleanup.ps1` removed the remaining exact 2025 probe manifest; no probe manifests remained for 2023, 2024 or 2026. Existing test processes can still have the isolated probe loaded until closed, but the registration is removed and it will not load on the next startup.

This demonstrates feasibility on the four installed builds. It is not production release acceptance: only the active Home mode was tested per version, full restart/lifecycle coverage remains incomplete, and search-route changes can require theme reapplication. The discovery harness retries periodically (approximately 30 seconds), so automatic reapplication needs deliberate integration work. No models were opened or modified; Autodesk assets and production BIMaestro code were unchanged.

### Reproduce

1. Build and start the isolated probe for a selected version with `native/Build.ps1` and `native/Start.ps1` (see `native/README.md`).
2. Apply one variant with `Set-Experiment.ps1 -RevitVersion 2025 -Preset dark`, or run `Run-Matrix.ps1` after all four probes are active. The matrix queues each batch in parallel across versions, preserves exact results, and throws on missing/failed evidence.
3. Queue `-Preset restore` before ending the session. Extra grouping experiments use `Set-Enhancements.ps1`; restore these with `Set-Enhancements.ps1 -RevitVersion 2025 -Restore` as well. Run `native/Cleanup.ps1` to remove only the exact temporary probe registrations. Closing the test instances unloads the isolated probe.

### Further acceptance work

Record each item independently for each version and each offered Home mode. A built DLL or successful script return is not visual acceptance.

1. Capture baseline and identify only the visible Home browser (type, URL/title, component tree).
2. Confirm a scoped injected marker and obtain browser-side computed-style/geometry evidence.
3. Capture color, compact-card, expanded-name and rearrangement variants on the real Home page.
4. Exercise Home search, sort, list/grid and resize; confirm styling survives page rerender where intended.
5. Verify Autodesk Open/Create controls remain usable without saving or changing a model.
6. Remove the injected style/marker and confirm the original Home appearance returns.
7. Revisit Home and restart the dedicated test instance to check lifecycle behavior.

For every run, preserve its version, PID, active Home mode, timestamps, probe output, screenshot paths, errors, and restoration result. Use **pending**, **blocked**, **failed** or **verified** precisely. Never report cross-version support from one successful installation.

## Série 3 — 17 octobre 2026

Quatre propositions appliquées dans les accueils natifs : 2023 Atelier essentiel (compteurs simples de 34 px, aperçus de 88 px), 2024 Bureau de projets (registre avec aperçu 56 px et métadonnées alignées), 2025 Bibliothèque (cartes horizontales, aperçus 72 px), 2026 Index (lignes compactes, aperçus 44 px). Mode classique en 2023/2024 ; nouvelle Home activée en 2025/2026. Palette vert forêt BIMaestro. Sources `home-concepts-legacy-v3.js`, `home-concepts-modern-v3.js`, activation `Set-Concept.ps1 -RevitVersion YEAR -Generation 3`.

Captures réelles vérifiées dans `tmp/home-tests/concepts-v3/` : `2023-atelier.png`, `2024-bureau.png`, `2025-bibliotheque.png`, `2026-index.png`. Contrôles syntaxiques Node des trois scripts et git diff --check réussis. Aucun modèle ouvert ou enregistré pendant ces vérifications. Le processus 2024 a nécessité un redémarrage avant capture ; cause non établie et stabilité de longue durée non validée. Les manifestes temporaires de lancement ont été retirés. Il s'agit de prototypes de session, pas d'une livraison production.

### Catalogue Excel : preuve native en 2026

Source lue sans modification : `C:\Users\lemer\OneDrive\Documents\RevitLogs\Historique_Temps_Revit.xlsx`, feuille Historique_Temps_Revit, A1:S1490. 1 489 lignes d'activité, 309 chemins RVT/RFA uniques : 278 RVT et 31 RFA. Lecture/dédoublonnage 168–207 ms sur trois essais. Les chemins déplacés ou inaccessibles ne sont pas tous vérifiés ; la version journalisée signifie « ouvert avec », pas une garantie de compatibilité actuelle.

Prototype temporaire `catalog/home-catalog-probe.js`, injecté dans Home 2026 avec recherche nom/chemin et filtres. JSON : 0,2 ms ; construction détachée de 309 lignes : 2,5–3,2 ms ; filtrage : environ 0,2 ms ; montage initial/layout de 24 lignes : 4,3 ms. Ces mesures ne représentent pas le démarrage complet de Revit ni le décodage de toutes les images. Recherche UI Familles + Statue : 2 résultats, filtre 0,2 ms et montage/layout 2,2 ms. Captures dans `tmp/home-tests/catalog/home-catalog-native.png` et `home-catalog-search.png`, preuve chiffrée `native-search-evidence.json`. Panneau retiré après test, Home Index conservée.

Aperçus intégrés extraits en lecture seule : 12/12 RFA et 3/12 RVT de l'échantillon, 128x128. Fallback icône indispensable ; cache et chargement des seules lignes visibles recommandés. Aucune extraction complète des 309 miniatures au démarrage. Détails, bornes et preuves d'intégrité dans `tmp/home-tests/catalog/REPORT.md`. Le bouton d'ouverture des entrées ajoutées n'est pas implémenté : le test valide le catalogue, les aperçus disponibles, les filtres et la recherche.

## Bouton Accueil Test — intégration 17 octobre 2026

Entrée native du ruban BIMaestro > Couleur et information > Accueil Test. Fenêtre modale de cinq choix : Autodesk d’origine, Atelier essentiel, Bureau de projets, Bibliothèque, Index ; case indépendante pour le catalogue Excel. Le choix est explicite et persistant (`%APPDATA%/BIMaestro/HomeAppearance.test.json`). Aucun habillage ni lecture Excel au premier démarrage avant activation. Les quatre styles sont proposés sur les deux moteurs Home, indépendamment de la version de Revit. Application puis commande Revit ToggleHome depuis un document ouvert. Retour Autodesk disponible.

Sources de production : `BIMaestro/les couleurs/Home/`. Ressources JS embarquées par SharedItems.props. Initialisation/arrêt dans BIMaestroApp. Suivi du thème natif UIThemeManager.CurrentTheme par réflexion, sans dépendance directe empêchant 2023 de compiler. 2023 ne dispose pas du thème sombre natif et reste clair. La préférence de thème native n’est pas modifiée par le code de production.

Corrections : cartes modernes Bibliothèque uniformes, dont les deux Tour Eiffel ; sélection Fichiers récents classique alignée pleine largeur ; palette sombre pour les deux moteurs ; fenêtre de choix remplacée par des radios lisibles après découverte d’un ComboBox blanc en sombre. Restauration retire les seuls éléments/styles possédés. Garde URL exacte ModelBrowser/resource/index.html ou udabrowser.html avant toute mutation.

Tests : builds réels de la fonctionnalité sur les quatre API, puis quatre builds complets BIMaestro réussis. Le SDK système 9 ne peut pas compiler les installations Revit .NET 10 ; usage du SDK existant `tmp/dotnet10/dotnet.exe` pour 2025/2026. Avertissements préexistants de la solution conservés. Tests Node check-home-production et check-home-lifecycle réussis (4 styles x 2 thèmes x 2 moteurs). Instances natives 2023/2024/2025/2026 : quatre choix successifs chacun appliqués ; preuves des deux dernières passes round3.json/round4.json dans tmp/home-tests/integration. Captures light, dark, sélection croisée et restauration dans le sous-dossier screenshots. Le harnais utilise des préférences isolées et des manifestes temporaires supprimés après démarrage.

Historique intégré : service NPOI asynchrone, journal d’origine jamais écrit, cache taille/date et contrôle toutes les 30 secondes, max 200000 lignes lues / 10000 entrées exposées. Test C# réel : 309 chemins (278 RVT,31 RFA), lecture/index 592–632ms en tâche de fond, SHA256 Excel inchangé. Six miniatures déjà en cache reconnues ; les autres reçoivent une icône immédiate. Aucune génération massive ou vérification des chemins réseau au chargement. Recherche/pagination 24 et ouverture native via requête validée par liste de chemins connus, exécutée en contexte Revit Idling. Une famille d’exemple RAC_basic_sample_family.rfa ouverte avec succès depuis le catalogue en 2026, sans modification ni enregistrement. Les changements d’historique dus au logger normal d’ouverture ne sont pas une écriture du lecteur catalogue.

Installation : `scripts/home-tests/Install.ps1 -RevitVersion YEAR` copie un build par version dans un répertoire LocalAppData BIMaestro/Bin/RevitYEAR-HomeTest-HASH, sauvegarde le manifeste et vérifie SHA256. Les quatre manifestes ont été mis à jour ; les anciennes DLL sont conservées. Les préférences réelles n’étaient pas activées avant vérification finale du ruban.

Vérification finale : les quatre DLL postérieures à la dernière source C# contiennent exactement les quatre ressources JS courantes (SHA256 comparés, `integration/VerifyResources.ps1`). Rapport `tmp/home-tests/integration/final-resource-verification.json`. Manifestes installés vérifiés dans `final-installed.json`. Le vrai ruban BIMaestro des builds installés a été vérifié en 2024 clair et 2026 sombre ; captures `selector-light.png` / `selector-dark.png`. Aucun choix validé pendant ces captures : accueil Autodesk conservé. Dernière correction incluse : ToggleHome est proposé même depuis un espace de travail sans document ; description du choix en 14px et interligne20 ; catalogue en deux colonnes pour mieux lire les noms ; placeholder natif sombre explicite. Derniers builds sans erreur, avertissements existants conservés.

Test final sur la DLL installée 2026 (SHA256 1FDC6CDA76BBECD00270BAF2FB5AC22FABA5F8D811EAD074FEB371EF62B5F210) : bouton réel, choix Bibliothèque, case historique cochée, Appliquer depuis espace vide => retour automatique Home et309entrées visibles. Sélecteur réouvert : choix et case conservés. Retour Autodesk + case décochée validés depuis le même bouton ; configuration réelle finale Style=native,History=false. Captures selector-final-dark.png, final-2026-applied-history.png et final-2026-restored.png dans tmp/home-tests/integration/screenshots. Revit2026 laissé ouvert avec son accueil Autodesk pour l’essai utilisateur. Versions2023/24/25 installées finales prêtes au prochain lancement.

## Catalogue vérifié et miniatures à la demande — 19 octobre 2026

Le catalogue n’affiche plus les309chemins bruts. HomeHistoryCatalog vérifie les fichiers hors du thread Revit : existence, fichier local lisible (lecture d’un octet), exclusion des fichiers Offline/Recall pour éviter de déclencher un téléchargement OneDrive. Les chemins cloud non vérifiables sont masqués ; les chemins réseau sont vérifiés par une seule tâche bornée en attente350ms, sans lancer une cascade si le partage ne répond pas. Indisponible ne signifie pas nécessairement supprimé. Les chemins déplacés ne sont pas recherchés ailleurs et aucune déduction de « dernière sauvegarde » n’est inventée.

ConfigureVersion utilise la version hôte. Les entrées associées à une version supérieure dans le journal sont exclues, puis la version courante est prioritaire devant les précédentes. Il s’agit de versions connues du journal, pas d’une lecture garantie de la version du format actuel. Vérification initiale progressive, nouvel examen périodique même quand le classeur reste identique (cache5min), suppression du catalogue si disparition détectée pendant une demande d’aperçu. Le compteur porte sur les fichiers vérifiés affichables.

HomeEmbeddedPreview lit seulement les secteurs CFB utiles et l’aperçu PNG intégré ; aucune API Revit, aucun rendu, aucune ouverture de document, aucun enregistrement d’image ou de modèle. Bornes12Mo lus,4Mo d’aperçu,2Mo d’annuaire, budget de traitement coopératif350ms ; une lecture disque bloquée ne peut pas être interrompue par ce budget, donc tout tourne en arrière-plan et les réseaux sont exclus de l’extraction. PNG validé ou icône de secours. Cache mémoire plafonné256entrées/16Mo de chaînesbase64, invalidé par taille/date ; absence également mémorisée.

IntersectionObserver demande les miniatures des seules lignes visibles, huit requêtes maximum par passage du pont ; un seul worker. Le pont envoie le catalogue uniquement quand sa révision change et attend l’accusé du navigateur, évitant de retransmettre les mêmes images chaque seconde. Les arrivées de miniatures conservent recherche, filtre et défilement. La restauration annule les demandes encore présentes côté page.

Vérification C# réelle2025 :309journalisés→120fichiers lisibles(96RVT,24RFA),185indisponibles masqués,4versionssupérieures connues exclues. Nouvelle exécution : lecture/index/validation596ms,24aperçus demandés et terminés dont4PNG en56ms. Classeur inchangé, chemins inconnus rejetés, mémo conservé après actualisation forcée. Échantillon d’extraction séparé :15PNG/24fichiers sans changement des sources. Aucun modèle ouvert pour ces tests.

Test navigateur réel Edge sans interface : `node scripts/home-tests/check-history-browser.cjs` vérifie les deux DOM Home en clair/sombre, demandes limitées au viewport, pagination24, remplacement parimage, recherche, position de défilement conservée, requête d’ouverture et restauration. Les quatrecontextes passent. Le test ne remplace pas la validation d’une session native et ne lance aucun fichier Revit.

Livraison du19octobre : builds production et intégration2023–2026 sans erreur, ressources embarquées comparées par SHA256 (`validated-resource-verification.json`). Quatre builds installés via Install.ps1 avec sauvegarde des manifestes ; activation au prochain redémarrage Revit, préférences utilisateur conservées. Tests Edge répétés après la dernière modification : quatre contextes passent. Nouvelle mesure en parallèle de la copie d’installation : validation2,42s en arrière-plan,24aperçus en98ms ; les lectures précédentes étaient autour de0,6–0,8s. Le cacheJSON est immédiat et les temps dépendent de la charge disque. Détails `tmp/home-tests/catalog/validated-performance-20261019.json`. Le compteur reste120en2025 ; aucun changement du journal pendant le test et aucun traitement de modèle Revit.

## Libellés sobres —19octobre2026

« Historique BIMaestro » et son nom accessible deviennent « Votre historique ». Suppression du préfixe de marque dans les quatre accroches de présentation, en Home classique et nouvelle. Source des familles enregistrées affichée « Familles enregistrées », titre du sélecteur « Personnaliser l’accueil · Test » et erreur de journal sans marque répétée. Identifiants, ressources, chemins réels et données des fichiers inchangés. Tests de portée/application/restauration JS réussis. La proposition de volet repliable avec recherche toujours visible est discutée ; aucun changement d’interaction à ce stade.

## Volet historique repliable — 19 octobre 2026

L’historique démarre replié avec le chevron, le titre et la recherche visibles. Le choix explicite ouvert/fermé est conservé dans le stockage local du navigateur Home. Une recherche non vide ouvre les résultats sans remplacer ce choix enregistré. Les miniatures ne sont demandées que lorsque la liste est dépliée et visible. Tests navigateur : classique/nouveau, clair/sombre, rechargement, recherche, pagination, défilement, ouverture et restauration.

## Recherche unique en accueil classique — 19 octobre 2026

Quand la personnalisation classique et l’historique sont actifs, une seule recherche reste visible sous le titre principal. Le sélecteur Récents / Tout l’historique conserve la saisie, pilote le volet et choisit la source filtrée. Le chevron synchronise aussi cette portée. Les filtres des cartes Revit sont masqués pendant la recherche dans l’historique. En l’absence de personnalisation classique, le champ autonome d’historique reste disponible. `check-unified-search.cjs` vérifie les deux portées, la conservation du texte, le chevron, la restauration et la réapplication d’un style en clair/sombre.
