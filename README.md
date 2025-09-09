# 🎼 BIMaestro

![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)
![Revit Version](https://img.shields.io/badge/Revit-2023/24/25-blue)
![Status](https://img.shields.io/badge/status-actif-green)

**BIMaestro** est un plugin Revit open source destiné à améliorer la productivité des dessinateurs/projeteurs.  
Il regroupe une suite d’outils modulaires (visualisation, IA, analyse, automatisation…) pour simplifier le BIM au quotidien.

---

## 📑 Sommaire

- [🎯 Objectif](#-objectif)
- [🧰 Fonctionnalités](#-fonctionnalités)
- [🖼️ Aperçu du Ruban](#-aperçu-du-ruban)
- [⚙️ Installation](#️-installation)
- [✍️ Auteur](#️-auteur)

---

## 🎯 Objectif

Optimiser le travail dans Revit avec un plugin polyvalent et évolutif :  
productivité, standardisation, intelligence artificielle et ergonomie au cœur de l’outil.

---

## 🧰 Fonctionnalités

### 🟩 Outils de Visualisation
- **Sélection d'éléments** intelligente par catégories
- **Peinture de matériaux** (matériau appliqué + peinture)
- **Ouvrir la vue du Plan** : navigation directe vers la feuille ou la vue
- **Réorienter Vue 3D** selon une face sélectionnée
- **Information d’élément** : matériaux, surface, volume, etc.
- **Export Nomenclature** : vers Excel ou PDF
- **Export DWG** : export automatique de vues ou feuilles


### 🛠️ Modification
- **Changer couleur élément** : personnalisation des vues (multi-vues, masquage ou réinitialisation)
- **Organisateur d’Éléments** : renommage intelligent dans le sens de lecture de la vue active
- **Auto Réservation** : crée des réservations automatiques pour tout objet traversant un mur (pas limité au MEP)
- **Outils Canalisations** : lancement de scripts Dynamo spécifiques
- **Gestion Excel** : export ou import de nomenclatures
- **Purge du plan** : nettoyage des vues, familles et nomenclatures inutiles
- **Brides auto** : ajoute ou retire automatiquement des brides aux extrémités des accessoires CVC
- **Connexion auto canalisations** : relie deux canalisations sélectionnées en générant le réseau optimal

### 🤖 Outils IA
- **Chatbot + élément** : assistant IA avec accès aux éléments Revit
- **Chatbot + screen** : capture d’écran + interaction IA
- **Correction de texte IA** : correction et reformulation de textes dans Revit
- **ScanText IA** : analyse complète et correction grammaticale sur toutes les vues/feuilles

### 📊 Analyse
- **Calcule des canalisations** : longueurs, volumes, accessoires, filtrage, export Excel
- **Qui a fais ça ??** : identifie les auteurs/modificateurs de vues et éléments
- **Analyse de Poids** : familles, DWG, PDF triés par taille, nombre d’instances, etc.
- **SmartCheck 3D** : détecte murs flottants, traversées MEP sans réservation et raccords ouverts

### 🧱 Spécifique aux familles
- **Purge des paramètres** inutiles dans une famille
- **Navigateur de Familles** avec aperçus et favoris
- **Traduction de paramètre IA** : via OpenAI, noms traduits automatiquement
- **Changement d'unité** : import/export des unités du projet

### 🎨 Couleur du projet
- **couleur Oui/Non** : active la colorisation dynamique du projet
- **couleur reset** : réinitialise les couleurs personnalisées
- **papa Noël** : effet visuel décoratif type guirlande colorée

---

## 🖼️ Aperçu du Ruban
<img width="2336" height="122" alt="image" src="https://github.com/user-attachments/assets/634778bd-dd70-4c3d-9cd5-c23019844c27" />

> 📷 *Capture du ruban Revit personnalisable de BIMaestro.*

---

## ⚙️ Installation

> 🚧 Ce plugin fonctionne également sous Revit 2025. Toutefois, les fonctionnalités **Information d’élément**, **Chatbot + élément** et **Purge des paramètres** n'y sont pas encore disponibles.

1. Cloner le dépôt :
   ```bash
   git clone https://github.com/hunterxgold62/BIMaestro.git
   
2. Ouvrir BIMaestro. sln avec Visual Studio 2022+
3. Compiler en mode Release
4. Copier le fichier .dll dans : %AppData%\Autodesk\Revit\Addins\2023\  (ou 2024/25)

## ✍️ Auteur
Paul Lemert
Développeur BIM | Dessinateur projeteur | Automatisation Revit & IA
