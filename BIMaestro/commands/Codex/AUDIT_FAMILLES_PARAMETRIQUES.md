# Évolution du moteur de familles paramétriques BIMaestro

Audit du 15 septembre 2026. Document de conception, sans implémentation de nouvelles fonctions dans cette passe. Périmètre : familles chargeables RFA, pour des objets variés, à partir de texte et/ou d'images. Les murs et autres familles système du projet relèvent d'outils distincts.

## Conclusion

Le principal obstacle actuel est le vocabulaire limité de la passerelle et son moteur de construction, pas une autorisation MCP manquante. Ajouter uniquement une case de visibilité laisserait les mêmes difficultés pour les formules, les occurrences et les composants optionnels. La prochaine évolution doit partager un modèle commun de paramètres, expressions, références, composants, représentations et essais.

« Présent dans le code » ne signifie pas « validé en exploitation » : les réseaux natifs et l'inclinaison ajoutés récemment restent à éprouver dans Revit. La compilation et les tests mathématiques ne valident pas le solveur de contraintes.

## Ce que l'audit du code établit

| Domaine | État actuel | Évolution à prévoir |
|---|---|---|
| Paramètres | 1–8 longueurs, jusqu'à 8 angles, matériaux ; tous de type | Registre commun : longueur, angle, entier, nombre, Oui/Non, texte, matériau et référence de type de famille ; unités, description, groupe et portée explicites |
| Formules | Coordonnées linéaires ; nombre imposé à `rounddown(span/pitch)` | Expressions arithmétiques et logiques, comparaisons, conditions, arrondis, trigonométrie ; valeurs calculées réutilisables |
| Occurrences | Pas de choix type/occurrence | Dimensions et options individuelles ; associations compatibles entre famille mère et enfants ; contrôle des dépendances type/occurrence |
| Visibilité conditionnelle | Non exposée | Paramètre Oui/Non manuel ou calculé, associé aux solides et composants ; règle commune à un groupe fonctionnel |
| Détail graphique | Aperçus 3D en détail fin ; pas de politique exposée | Grossier/moyen/fin, orientation des vues, sous-catégories fonctionnelles ; représentations alternatives |
| Dessin 2D | Pas de représentation symbolique dédiée | Lignes symboliques, symboles imbriqués, masquages si nécessaires, lisibilité en plan et plafond réfléchi |
| Types | Création d'un type Standard | Plusieurs types nommés, valeurs par type, contrôles de chacun ; catalogues pour les gammes importantes |
| Paramètres partagés | Non exposés | Réutilisation des GUID de l'entreprise pour nomenclatures et étiquettes ; aucune recréation avec un GUID aléatoire |
| Références | Plans internes, coordonnées orthogonales, changement de signe interdit pendant les essais | Origine et points d'accroche choisis, plans/lignes stables, centrage, égalités, décalages, rayons et pivots ; prises de cote utiles dans le projet |
| Formes paramétriques | Extrusions rectangulaires, ouvertures rectangulaires traversantes | Profils circulaires/polygonaux contraints, cylindres/tubes, révolutions, balayages et raccordements natifs selon faisabilité |
| Formes détaillées | Primitives, lofts et évidements, mais solides fixes | Composition mixte explicitant quelles pièces sont pilotées et lesquelles restent fixes ; aucune promesse de paramétrisation automatique d'un solide quelconque |
| Angles | Barres rectangulaires en réseau, un axe X/Y/Z, 1–89° | Module d'articulation réutilisable hors réseau ; pivot choisi, rotation d'un sous-ensemble ; autres plages seulement après validation des singularités |
| Réseaux | Rectilignes, barres rectangulaires, 2–200 exemplaires | Motif imbriqué quelconque compatible, compte ou pas pilotant, pas maximal, répartition centrée, marges, radial, deux directions, cas 0/1 |
| Familles imbriquées | Barre technique générée en interne | Composants réutilisables et interchangeables ; paramètre de type de famille ; choix partagé/non partagé selon les nomenclatures |
| Hébergement | Gabarit générique puis catégorie choisie | Choix initial du gabarit : libre, face, plan de travail, mur, plafond, ligne ; orientation, décalage et comportement avec l'hôte |
| Catégories | Generic, electrical, mechanical, furniture, plumbing | Catégories adaptées, dont bouches d'aération ; contrôle de compatibilité gabarit/catégorie/comportement |
| Connecteurs | Aucun | Air/eau/électricité selon l'objet : position, direction, section, système et associations dimensionnelles ; vérification en projet |
| Matériaux | Couleur, transparence et paramètre de type | Réutilisation d'une bibliothèque, apparence de rendu, textures et chemins portables ; propriétés physiques seulement à partir de données fiables |
| Réservations | Trous rectangulaires intégrés au profil ; booléens fixes dans le mode riche | Vides natifs distincts, coupes identifiées, réservation/hôte selon catégorie ; contrôles lorsque la forme change |
| Révision | Nouveau RFA ; descriptif enregistré, potentiellement ancien après retouche manuelle | Identifiants stables des composants, lecture des paramètres/formules actuels, différence entre demande et existant, correction ciblée sans perdre les réglages |
| Validation | Valeurs initiales, variations individuelles/combinées, géométrie ; trois vues 3D | Seuils et bornes, affichage, types, occurrences, chargement/rechargement, hôtes, raccordements, poids et temps de régénération |

Constats tirés de `CodexParametricDesign.cs`, `CodexParametricBuilder.cs`, `CodexParametricArrayBuilder.cs`, `CodexAngularBarBuilder.cs`, `CodexFamilyBuilder.cs`, `CodexFamilyDesign.cs` et `CodexRevitBridge.cs`. L'API locale Revit 2023 expose déjà `FamilyManager.SetFormula`, les paramètres d'occurrence et partagés, `GenericForm.SetVisibility`, `FamilyElementVisibility`, les courbes symboliques, balayages, raccordements, révolutions et connecteurs. La présence d'une méthode ne garantit pas une construction contrainte robuste dans tous les gabarits.

## Visibilité : trois besoins à séparer

1. **Option d'objet** : afficher un capot, des fixations ou une poignée ; réglage de type ou d'occurrence.
2. **Règle dimensionnelle** : afficher un renfort uniquement au-delà d'une portée, ou masquer une pièce au-delà d'un seuil. La règle est portée par une formule native, autonome après fermeture de Codex.
3. **Représentation graphique** : enveloppe simplifiée en grossier, composants principaux en moyen, vis et détails en fin ; complément 2D adapté aux vues.

Exemples de règles, à adapter à l'objet et sans les imposer à toutes les familles :

| Résultat attendu | Paramètre Oui/Non et formule Revit |
|---|---|
| Renfort visible si la largeur dépasse 1 000 mm | `AfficherRenfort = Largeur > 1000 mm` |
| Capot masqué uniquement au-dessus de 1 200 mm | `AfficherCapot = not(Largeur > 1200 mm)` |
| Fixations visibles si l'option est activée et la profondeur suffisante | `AfficherFixations = and(OptionFixations, Profondeur > 80 mm)` |

Ces mécanismes sont documentés par Autodesk : [visibilité des géométries](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-Customize/files/GUID-749AEB1E-1B01-46BC-9790-3473BC769211.htm), [conditions dans les formules](https://help.autodesk.com/cloudhelp/2022/ENU/Revit-Model/files/GUID-A0FA7A2C-9C1D-40F3-A808-73CD0A4A3F20.htm) et [API FamilyElementVisibility](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/fae58e2d-817c-77f6-1747-58b0a4e01c7a.htm).

La règle dimensionnelle et le niveau de détail doivent se combiner : une fixation désactivée ne doit pas réapparaître en détail fin. Prévoir aussi des représentations mutuellement exclusives pour éviter de superposer un volume simplifié et ses détails.

**Masquer n'est pas supprimer.** La géométrie cachée continue d'exister, et Autodesk précise qu'elle peut encore participer à des jonctions. On ne doit pas utiliser la visibilité pour contourner une épaisseur nulle, une auto-intersection ou une contrainte impossible. Pour une variante absente, maintenir une géométrie cachée valide ou utiliser un mécanisme de variante approprié, puis vérifier les quantités et exports concernés. Une visibilité coupée n'est pas une garantie de retrait d'une nomenclature ni de réduction du poids du RFA.

Les niveaux grossier/moyen/fin sont des représentations graphiques. Ils ne constituent pas à eux seuls un engagement de LOD BIM : précision, informations et usage attendu de la famille doivent également être définis. Une formule de famille ne reçoit pas automatiquement tout l'environnement du projet ; une condition de collision avec un autre objet nécessite une lecture et une analyse spécifiques. Elle n'est pas équivalente à `Largeur > seuil`.

## Architecture proposée pour la prochaine évolution

### Un descriptif versionné et partagé par les outils

Remplacer les listes spécialisées par un registre de paramètres typés, avec identifiant stable, nom affiché, unité, portée type/occurrence, valeur ou formule, groupe, description et domaine d'essai. Conserver un adaptateur pour les descriptifs déjà enregistrés.

Les contraintes, matériaux, visibilité, répétitions et composants référencent ce même registre. Les paramètres internes sont dédupliqués et distincts des réglages utilisateur ; ne pas créer une cote pilotante pour chaque coordonnée constante. Un paramètre purement fonctionnel, de visibilité ou de donnée ne doit plus être refusé parce qu'il ne pilote pas une coordonnée.

Prévoir un graphe d'expressions typées : détection des cycles, paramètres inconnus, unités incompatibles, divisions par zéro et dépendances type/occurrence interdites. Traduire vers les formules Revit, avec validation native finale. Les règles métier restent déclaratives ; cette évolution ne nécessite pas d'exécuter du C# arbitraire fourni par le modèle.

Un domaine minimum/maximum dans le descriptif ne borne pas automatiquement les saisies manuelles dans Revit. Distinguer une valeur demandée et une valeur appliquée par formule lorsque le besoin prévoit un bornage, ou documenter le domaine valide et son comportement d'erreur. Ne jamais modifier silencieusement une dimension demandée pour faire passer les essais. Les changements de topologie (trou qui rejoint un bord, élément qui disparaît, inversion de pivot) demandent des variantes ou des constructions dédiées, pas seulement une formule supplémentaire.

### Des composants avec références, comportements et représentations

Chaque composant possède ses références de placement, sa géométrie, son matériau, sa visibilité et ses niveaux de détail. Les mêmes réglages s'appliquent aux formes simples et aux composants imbriqués. Le réseau répète un composant, pas exclusivement une barre codée en dur. Les contraintes s'appuient autant que possible sur des références stables plutôt que sur des faces susceptibles de disparaître pendant une variation.

Pour les répétitions, expliciter : distance entre centres ou vide libre, marges en bout, inclusion des extrémités, centrage, quantité souhaitée ou calculée et règle d'arrondi. « Une pièce tous les 50 mm » ne suffit pas toujours à déterminer ces choix. Plusieurs variantes peuvent partager un même squelette et avoir des paramètres d'affichage distincts.

### Un état réel des capacités

La passerelle doit renvoyer la version de Revit, le gabarit et les fonctions disponibles. Distinguer : non implémenté, implémenté mais expérimental, testé dans une version précise, incompatible avec le document. Le modèle doit lire cet état au début et avant d'annoncer une limitation ; éviter les phrases figées devenues fausses après mise à jour.

Avant de construire, produire un contrat court : paramètres modifiables, constantes, formules, répétitions, options, détail graphique, catégorie et hébergement. Poser une à trois questions uniquement sur les décisions déterminantes absentes du contexte. Après création, fournir le résultat réel des essais et les limites restantes.

## Version de Revit : décision retenue

Décision utilisateur corrigée le 15 septembre 2026 : **viser Revit 2023 et les versions suivantes**. Conserver le moteur commun compatible avec l'API 2023 et les builds .NET Framework 4.8 pour 2023/2024. Préparer séparément les builds .NET 8 pour 2025+. La cible de conception ne certifie pas la compatibilité de versions non testées. L'état de l'implémentation est décrit dans [V1_IMPLEMENTATION.md](V1_IMPLEMENTATION.md) ; le tableau initial ci-dessus reste un audit des besoins.

La configuration Release actuelle cible .NET Framework 4.8 et l'API Revit 2023 ; une configuration 2024 existe. Plusieurs versions sont installées sur le poste, ce qui ne prouve pas laquelle exécute le complément actuellement.

Revit 2025 introduit les [réseaux de zéro et un élément](https://help.autodesk.com/cloudhelp/2025/ENU/Revit-WhatsNew/files/GUID-97BA5D8B-C737-47B4-9A69-7C7BA27DB19D.htm). Pour le contrat commun 2023+, les cas zéro/un utilisent une visibilité conditionnelle et une occurrence isolée : le réseau technique conserve au moins deux membres. Le rapport doit distinguer nombre demandé, nombre visible et géométries conservées. Ne pas assimiler des objets cachés à zéro objet réel. Une optimisation native spécifique 2025+ reste une extension à éprouver.

Revit 2025 change également de runtime vers .NET 8 ([documentation Autodesk](https://help.autodesk.com/cloudhelp/2025/CHS/Revit-API/files/Revit_API_Developers_Guide/Introduction/Getting_Started/Using_the_Autodesk_Revit_API/Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Using_the_Autodesk_Revit_API_NET8_Update_html.html)). Prévoir des builds et validations par génération de Revit, sans promettre qu'une DLL .NET 4.8 convient à toutes les versions suivantes. Ce point concerne l'ensemble du complément et doit être traité avant une promesse de compatibilité élargie.

## Ordre d'implémentation recommandé

**Prérequis — cible Revit 2023+.** Conserver le build 2023, vérifier le build 2024 et préparer séparément la migration .NET 8 du complément pour 2025+. Vérifier les dépendances, le ruban, WPF et l'exécution par ExternalEvent dans chaque version. Annoncer une version compatible seulement après compilation et essais dans cette version.

**Ensemble A — fondation paramétrique commune.** Registre typé, formules, type/occurrence, types nommés, visibilité conditionnelle, détail graphique, représentations 2D, références stables et essais de seuils. Généraliser le descriptif des composants et préparer les associations imbriquées dans ce même ensemble. Valider aussi les réseaux et angles déjà codés avant de les considérer fiables.

**Ensemble B — géométrie et assemblages.** Profils contraints plus variés, articulations, composants imbriqués interchangeables, motifs de réseaux génériques, répartition et cas 0/1, vides natifs. Réutiliser la fondation A plutôt que créer des outils spécifiques à une grille, une armoire ou un transformateur.

**Ensemble C — usage BIM dans le projet.** Gabarits et hébergement, catégories spécialisées, connecteurs, paramètres partagés, matériaux enrichis, révision ciblée et tests de nomenclatures. Les contrats de ces fonctions doivent être prévus dès la conception, leur activation suit les essais natifs.

Cet ordre forme un plan unique ; il ne suppose pas une demande utilisateur distincte par petite fonction. Les composants adaptatifs à points de placement, surfaces libres complexes et formules de fabrication spécialisées restent des extensions identifiées, pas une promesse de couverture totale de Revit.

## Critères de réception

Prévoir au moins cinq familles témoins pour éviter une conception centrée sur la ventilation : grille à lames, étagère avec renfort optionnel, bride circulaire à perçages répétés, armoire avec portes et accessoires interchangeables, équipement avec raccordement MEP.

Pour chaque fonction concernée :

- Tester minimum, nominal, maximum, variantes et combinaisons pertinentes ; revenir à l'état initial.
- Tester juste sous le seuil, exactement au seuil et juste au-dessus ; faire varier les réseaux autour des changements de nombre et des cas 0/1/2 lorsque pris en charge.
- Vérifier forme et contraintes même quand une pièce est cachée ; mesurer séparément géométrie construite, représentation visible et quantités.
- Charger dans un projet d'essai ; contrôler plan, plafond réfléchi, élévation, coupe et 3D, avec grossier/moyen/fin. Les images du seul éditeur de familles ne suffisent pas.
- Placer deux occurrences : changer un réglage d'occurrence sur l'une, puis un réglage de type ; vérifier les effets attendus sur chacune.
- Vérifier changement de type, matériaux, hôte, miroir et rotation, puis fermeture/réouverture. Tester les références d'accrochage et les raccordements exposés.
- Vérifier les nomenclatures et exports requis pour les composants optionnels ; mesurer poids du RFA, temps de régénération et impact des répétitions, sans promettre un gain de performance lié à la seule visibilité.
- En cas d'échec : annuler l'opération, identifier composant, paramètre, valeur, formule, version et message Revit. Ne pas livrer silencieusement une famille figée à la place d'une famille paramétrique.

Le rapport de validation doit distinguer les tests du descriptif, les tests natifs et les vérifications visuelles. Autodesk recommande précisément de tester les familles dans l'éditeur et dans un projet, avec différents types, vues, matériaux et hôtes : [Test the Family](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-Customize/files/GUID-6EA8BEC0-4E78-4E68-BDD4-3E9AA2672F07.htm).

Autres références utilisées : [paramètres et paramètres partagés](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-Model/files/GUID-AEBA08ED-BDF1-4E59-825A-BF9E4A871CF5.htm), [catégorie, hôte et partage des composants](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-Customize/files/GUID-68EFCA67-4913-4E00-AB9E-F2E6A7BEF8C6.htm), [paramètres de rapport et restrictions de références](https://help.autodesk.com/cloudhelp/2016/ENU/RevitLT-Model/files/GUID-DBC31A77-813A-47E0-8EFA-B6D821F75EDB.htm). Les paramètres de rapport doivent constituer une extension contrôlée liée à l'hôte ; ils ne permettent pas de piloter librement la famille à partir de toute géométrie voisine.
