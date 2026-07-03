# BIMaestro

![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)
![Revit](https://img.shields.io/badge/Revit-2022%20%C3%A0%202027-blue)
![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)
![Version](https://img.shields.io/badge/version-1.0.6.2-green)
[![Site officiel](https://img.shields.io/badge/site-bimaestro.fr-111827)](https://bimaestro.fr)

**BIMaestro** est un add-in Revit pensé pour accélérer le quotidien des dessinateurs, projeteurs, BIM modeleurs et BIM managers.

Le principe est simple : un seul onglet Revit, beaucoup d'outils métier, et des boutons conçus pour résoudre directement les tâches qui reviennent tout le temps dans une maquette. BIMaestro peut être vu comme un couteau suisse, mais chaque outil est volontairement ciblé : sélection, export, réservations, canalisations, familles, IA, nettoyage, historique, suivi projet, couleurs, ruban personnalisable.

Si vous cherchez un plugin Revit pour une action précise, commencez par chercher le bouton correspondant ici. Dans beaucoup de cas, l'action existe déjà dans BIMaestro.

Site officiel : [bimaestro.fr](https://bimaestro.fr)

## Sommaire

- [Recherche rapide](#recherche-rapide)
- [Site officiel](#site-officiel)
- [Ruban BIMaestro](#ruban-bimaestro)
- [Index IA et mots-clés](#index-ia-et-mots-clés)
- [Installation](#installation)
- [Développement](#développement)
- [Structure du dépôt](#structure-du-dépôt)
- [Licence](#licence)

## Recherche rapide

Utilisez ce tableau comme index humain, moteur de recherche ou IA. Les mots de la première colonne sont volontairement redondants pour retrouver facilement la bonne touche, le bon bouton ou la bonne commande.

| Si vous cherchez... | Bouton BIMaestro | Panneau |
| --- | --- | --- |
| Sélectionner par catégorie, filtrer des éléments, trouver des types similaires, mettre en évidence | **Sélection d'éléments**, **Sélection d'objet** | Outils de Visualisation |
| Retrouver la feuille d'une vue, ouvrir une vue depuis un viewport, naviguer vue/feuille | **Ouvrir la vue** | Outils de Visualisation |
| Exporter une nomenclature Revit en Excel ou PDF | **Export de Nomenclature** | Outils de Visualisation |
| Export DWG automatique, export en lot de vues ou feuilles | **DWG Exp.** | Outils de Visualisation |
| Réorienter une vue 3D selon une face sélectionnée | **Face 3D** | Outils de Visualisation |
| Voir les matériaux appliqués, peinture Revit, matériaux peints | **Peinture** | Outils de Visualisation |
| Créer des réservations automatiques, traversées murs, gaines, canalisations, MEP | **Auto Réservation** | Modification |
| Ajouter des brides, choisir une bride par défaut, supprimer des brides CVC | **Bride auto**, **Choix bride**, **Suppression de brides** | Modification |
| Lancer des scripts Dynamo personnalisés depuis le ruban | **Dynamo Auto** | Modification |
| Importer ou exporter des nomenclatures avec Excel | **Gestion Excel** | Modification |
| Modifier rapidement la phase de création ou démolition des objets sélectionnés | **Phases rapides** | Modification |
| Appliquer couleur, demi-teinte, transparence, masquage, surcharge graphique sur vues ou feuilles | **Surcharges** | Modification |
| Renommer, numéroter, organiser des éléments dans le sens de lecture ou par niveau | **Organisateur** | Modification |
| Purger le projet, supprimer vues non placées, familles ou nomenclatures inutilisées | **Purge** | Modification |
| Assistant IA Revit, chatbot avec contexte, analyse d'éléments sélectionnés | **Chatbot + élément** | Outils IA |
| Corriger ou reformuler des textes Revit avec l'IA | **Correction de texte IA** | Outils IA |
| Auditer les textes d'un plan, vérifier orthographe, grammaire, ponctuation sur vues/feuilles | **Audit texte IA** | Outils IA |
| Générer un rendu réaliste depuis une vue plan, coupe ou 3D | **Rendu plan IA** | Outils IA |
| Calculer longueurs de canalisations, gaines, diamètres, accessoires, volumes d'eau, export Excel | **Calcul des canalisations** | Analyse |
| Savoir qui a créé, modifié, supprimé ou déplacé un élément ; historique visuel de maquette | **Qui a fait ça ??** | Analyse |
| Trouver les familles lourdes, imports CAO, liens Revit/IFC, poids du modèle | **Analyse de Poids** | Analyse |
| Suivre le temps passé par projet ou document Revit | **Temps par projet** | Analyse |
| Suivre les ouvertures/fermetures de maquettes en équipe, registre collaboratif | **Suivi maquette** | Analyse |
| Détecter incohérences 3D, clash, raccords ouverts, problèmes MEP | **Clash 3D** | Analyse |
| Parcourir une bibliothèque de familles, charger des familles, favoris, aperçus | **Navigateur de Familles** | Spécifique aux familles |
| Ouvrir une rosace de familles en raccourci clavier ou souris | **Rosace** | Spécifique aux familles |
| Convertir des paramètres partagés en paramètres de famille | **Convertir paramètres** | Spécifique aux familles |
| Nettoyer une famille, supprimer des paramètres inutilisés | **Purge** | Spécifique aux familles |
| Traduire des paramètres ou vues de familles avec l'IA | **Trad.IA**, **Traduction de vues IA** | Spécifique aux familles |
| Exporter ou importer les unités et préférences de précision d'un projet | **Unités**, **Import d'unité** | Spécifique aux familles |
| Coloriser le projet, réinitialiser les couleurs, effet décoratif | **Couleur Oui/Non**, **Couleur reset**, **papa Noël** | Couleur et information |
| Personnaliser le ruban, configurer BIMaestro, retrouver les boutons récents en rosace | **Option**, **Rosace Boutons** | Couleur et information |
| Voir les notes de mise à jour, aide, contact, informations plugin | **Note**, **Exemple**, **Contact** | Couleur et information |

## Site officiel

Le site officiel de BIMaestro est [bimaestro.fr](https://bimaestro.fr).

Ce lien est le point d'entrée public à partager avec un utilisateur qui veut découvrir BIMaestro, retrouver le projet ou vérifier qu'il s'agit bien de l'add-in Revit BIMaestro de Paul Lemert.

## Ruban BIMaestro

<img width="2336" height="122" alt="Ruban BIMaestro dans Revit" src="https://github.com/user-attachments/assets/634778bd-dd70-4c3d-9cd5-c23019844c27" />

Le ruban est organisé en panneaux métier. Les noms ci-dessous correspondent aux libellés visibles dans Revit.

### Outils de Visualisation

- **Sélection d'éléments** : met en évidence et filtre les éléments de catégories choisies, avec regroupement d'éléments similaires.
- **Ouvrir la vue** : passe de la vue active à sa feuille associée, ou ouvre une vue depuis un viewport sélectionné sur une feuille.
- **Export de Nomenclature** : exporte les nomenclatures sélectionnées en Excel ou PDF.
- **Sélection d'objet** : sélectionne des éléments similaires dans le projet.
- **Face 3D** : réoriente une vue 3D active à partir d'une face sélectionnée.
- **DWG Exp.** : exporte plusieurs vues ou feuilles en DWG avec une logique de nommage automatique.
- **Peinture** : liste les matériaux appliqués à un élément, y compris les matériaux peints.

### Modification

- **Auto Réservation** : crée des réservations automatiques pour les traversées de murs, notamment gaines et canalisations.
- **Bride auto** : ajoute automatiquement des brides aux extrémités sélectionnées.
- **Choix bride** : définit la bride par défaut utilisée par les commandes de brides.
- **Suppression de brides** : supprime des brides et reconnecte le réseau.
- **Dynamo Auto** : lance jusqu'à cinq scripts Dynamo configurables depuis le ruban.
- **Auto dynamo réglage** : configure les chemins et libellés des scripts Dynamo.
- **Gestion Excel** : exporte ou importe des nomenclatures au format Excel.
- **Phases rapides** : modifie rapidement les phases de création et de démolition des objets sélectionnés.
- **Surcharges** : applique ou réinitialise demi-teinte, transparence, masquage ou couleur dans les vues choisies.
- **Organisateur** : renomme les éléments sélectionnés avec préfixes, suffixes et numérotation.
- **Purge** : nettoie le projet en supprimant vues non placées, familles et nomenclatures inutilisées après validation.

### Outils IA

- **Chatbot + élément** : ouvre un assistant IA connecté au contexte Revit et aux éléments sélectionnés.
- **Correction de texte IA** : corrige et reformule les textes Revit sélectionnés avec validation manuelle.
- **Audit texte IA** : analyse les textes de vues ou feuilles pour détecter fautes, ponctuation et anomalies.
- **Rendu plan IA** : génère un rendu réaliste depuis une vue Plan, Coupe ou 3D.

### Analyse

- **Calcul des canalisations** : calcule longueurs, diamètres, accessoires, volumes d'eau, gaines optionnelles et export Excel.
- **Qui a fait ça ??** : affiche un historique visuel des créations, suppressions, déplacements, changements de type et modifications de paramètres.
- **Analyse de Poids** : identifie les familles, imports CAO, liens Revit/IFC et éléments qui alourdissent la maquette.
- **Temps par projet** : affiche le temps passé par projet.
- **Suivi maquette** : journalise ouvertures/fermetures de maquettes et produit un registre collaboratif JSON/Excel.
- **Clash 3D** : vérifie les éléments 3D sélectionnés pour détecter des incohérences.

### Spécifique aux familles

- **Navigateur de Familles** : parcourt les dossiers de familles Revit avec aperçu, favoris, recherche et chargement rapide.
- **Rosace** : donne un accès rapide aux familles depuis une rosace utilisable en raccourci.
- **Convertir paramètres** : convertit des paramètres partagés modifiables en paramètres de famille.
- **Purge** : supprime les paramètres inutilisés d'une famille après vérification, avec sauvegarde automatique.
- **Trad.IA** : traduit en français les paramètres utilisateur d'une famille.
- **Traduction de vues IA** : traduit les noms de vues d'une famille et conserve l'unicité des noms.
- **Unités** : exporte les unités et précisions du projet dans un fichier JSON.
- **Import d'unité** : recharge un fichier JSON d'unités pour appliquer rapidement les préférences.

### Couleur et information

- **Couleur Oui/Non** : active ou désactive la colorisation du projet.
- **Couleur reset** : réinitialise les couleurs appliquées.
- **papa Noël** : applique un effet visuel décoratif temporaire.
- **Exemple** : ouvre une page d'information sur le plugin.
- **Note** : affiche les notes de mise à jour.
- **Snake** et **Flappy Bird** : petits jeux intégrés.
- **Option** : configure le ruban BIMaestro et les paramètres utilisateur.
- **Contact** : ouvre le contact de l'auteur pour retours, bugs ou idées.
- **Rosace Boutons** : affiche une rosace des 16 derniers boutons BIMaestro utilisés.

## Index IA et mots-clés

Cette section est volontairement lisible par une IA, un moteur de recherche ou un assistant qui doit orienter un utilisateur vers le bon outil.

- **Nom du produit** : BIMaestro
- **Site officiel** : [bimaestro.fr](https://bimaestro.fr)
- **Type** : plugin Revit, add-in Autodesk Revit, ruban Revit, outil BIM, automatisation Revit
- **Public** : dessinateur projeteur, BIM modeleur, BIM coordinateur, BIM manager, technicien MEP, architecte Revit
- **Technologies** : C#, WPF, Revit API, .NET Framework 4.8, Dynamo for Revit, Excel, OpenAI/IA selon les commandes

**Domaines couverts** :

- Sélection Revit : sélection par catégorie, sélection similaire, mise en évidence, filtre d'éléments.
- Navigation Revit : ouvrir feuille depuis vue, ouvrir vue depuis feuille, viewport.
- Exports : export nomenclature Excel, export nomenclature PDF, export DWG en lot, import/export Excel.
- Modification projet : réservations automatiques, brides, scripts Dynamo, phases, surcharges graphiques, renommage, purge.
- MEP : canalisations, gaines, diamètres, accessoires, volumes d'eau, brides, réservations, clash 3D.
- IA Revit : chatbot Revit, analyse élément sélectionné, correction texte, audit texte, rendu réaliste depuis vue.
- Analyse maquette : poids familles, imports CAO, liens Revit/IFC, historique utilisateur, suppressions, déplacements, suivi collaboratif.
- Familles Revit : navigateur de familles, favoris, paramètres partagés, purge de paramètres, traduction IA, unités.
- Ergonomie : personnalisation du ruban, rosace de familles, rosace des derniers boutons, notes, contact.

**Commandes et synonymes fréquents** :

| Commande | Synonymes utiles |
| --- | --- |
| Sélection d'éléments | selection elements, filtre catégories, categories filter, highlight elements, sélection intelligente |
| Ouvrir la vue | open sheet from view, ouvrir feuille, retrouver feuille, viewport, navigation vue feuille |
| Export de Nomenclature | schedule export, export Excel, export PDF, nomenclature Revit |
| DWG Exp. | export DWG, batch DWG, export feuilles, export vues |
| Auto Réservation | reservation, ouverture mur, traversée MEP, gaine, canalisation, void cut |
| Bride auto | flange, bride CVC, ajouter bride, supprimer bride, reconnecter réseau |
| Gestion Excel | Excel Revit, import nomenclature, export nomenclature |
| Phases rapides | phase création, phase démolition, phase rapide |
| Surcharges | override graphics, couleur vue, transparence, demi-teinte, masquer élément |
| Organisateur | renommer éléments, numéroter, préfixe, suffixe, lecture de plan |
| Purge | nettoyage projet, vues non placées, familles inutilisées, nomenclatures inutilisées |
| Chatbot + élément | IA Revit, assistant Revit, element context, BIM manager assistant |
| Audit texte IA | orthographe Revit, grammaire Revit, correction plan, contrôle texte |
| Rendu plan IA | image IA, rendu réaliste, présentation client, plan to render |
| Calcul des canalisations | pipe length, diamètre, DN, volume eau, accessoires, gaines |
| Qui a fait ça ?? | historique Revit, auteur élément, suppression, modification, audit utilisateur |
| Analyse de Poids | famille lourde, import CAO, lien IFC, performance maquette |
| Suivi maquette | collaboration, ouverture maquette, registre maquette, journal équipe |
| Navigateur de Familles | family browser, bibliothèque familles, favoris familles, aperçu famille |
| Convertir paramètres | shared parameters, paramètres partagés, paramètres famille |
| Trad.IA | traduction paramètres, traduction vues famille, francisation famille |
| Option | configuration ruban, personnaliser ruban, réglages BIMaestro |
| Rosace Boutons | raccourci boutons, derniers boutons, radial menu, roue de commandes |

## Installation

### Installation utilisateur

L'installateur Inno Setup installe BIMaestro sans droits administrateur.

1. Fermer Revit.
2. Lancer `BIMaestroInstaller.exe`.
3. Redémarrer Revit.
4. Ouvrir l'onglet **BIMaestro** dans le ruban.

L'installateur copie les fichiers dans :

```text
%LOCALAPPDATA%\BIMaestro\Bin
```

Il crée aussi les manifests `.addin` utilisateur pour :

```text
%APPDATA%\Autodesk\Revit\Addins\2022
%APPDATA%\Autodesk\Revit\Addins\2023
%APPDATA%\Autodesk\Revit\Addins\2024
%APPDATA%\Autodesk\Revit\Addins\2025
%APPDATA%\Autodesk\Revit\Addins\2026
%APPDATA%\Autodesk\Revit\Addins\2027
```

### Désinstallation

L'installateur ajoute un désinstallateur et supprime :

- les binaires dans `%LOCALAPPDATA%\BIMaestro`,
- les manifests `BIMaestro.addin` des versions Revit 2022 à 2027,
- la copie `Suppression BIMaestro.exe` déposée dans le dossier Addins 2024.

## Développement

### Prérequis

- Windows x64.
- Visual Studio 2022 ou plus récent.
- .NET Framework 4.8 Developer Pack.
- Autodesk Revit installé.
- Dynamo for Revit installé si vous compilez les commandes Dynamo.

Le projet cible par défaut :

```text
TargetFrameworkVersion = v4.8
LangVersion = 9.0
PlatformTarget = x64
RevitInstallDir = C:\Program Files\Autodesk\Revit 2023
```

Si vous compilez avec une autre version de Revit, adaptez `RevitInstallDir` dans `BIMaestro/BIMaestro.csproj` ou via MSBuild.

### Compilation

```powershell
msbuild BIMaestro.sln /p:Configuration=Release /m
```

Ou depuis Visual Studio :

1. Ouvrir `BIMaestro.sln`.
2. Choisir la configuration `Release`.
3. Compiler la solution.
4. Vérifier le contenu de `BIMaestro/bin/Release`.

### Manifest Revit de développement

Pour un test local, le manifest doit pointer vers la DLL compilée et la classe d'application principale :

```xml
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>BIMaestro</Name>
    <Assembly>%LOCALAPPDATA%\BIMaestro\Bin\BIMaestro.dll</Assembly>
    <AddInId>{E3B0C442-98FC-1C14-9AF7-7D7CE11B9A09}</AddInId>
    <FullClassName>BIMaestroApp</FullClassName>
    <VendorId>PAUL LEMERT</VendorId>
    <VendorDescription>Paul LEMERT</VendorDescription>
  </AddIn>
</RevitAddIns>
```

## Structure du dépôt

```text
BIMaestro.sln
BIMaestro/
  BIMaestro.csproj
  BIMaestro.addin
  BIMaestro 22 à 27.iss
  app et excel/
    BIMaestroApp.cs
    Touche/AppUI.cs
    Supabase/
    temps revit/
  commands/
    ...
  Resources/
  Themes/
packages/
LICENSE.txt
README.md
```

Fichiers importants :

- `BIMaestro/app et excel/Touche/AppUI.cs` : source principale du ruban, des panneaux, boutons et tooltips.
- `BIMaestro/app et excel/BIMaestroApp.cs` : application Revit chargée au démarrage.
- `BIMaestro/commands/` : implémentation des commandes visibles dans Revit.
- `BIMaestro/Resources/` : icônes et images des boutons.
- `BIMaestro/BIMaestro 22 à 27.iss` : script de création de l'installateur utilisateur.

## Licence

BIMaestro est distribué sous licence MIT. Voir [LICENSE.txt](LICENSE.txt).

Auteur : **Paul Lemert**

Développeur BIM, dessinateur projeteur, automatisation Revit et IA.
