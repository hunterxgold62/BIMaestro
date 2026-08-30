# ViewDeck — premier test Revit

Les tests STA de `ViewDeckHost` vérifient la réservation d'espace et la restitution de la grille WPF sans lancer Revit.

## Vérification manuelle dans Revit 2024

1. Charger la compilation `Release2024` par la méthode habituelle et redémarrer Revit. Dans « Couleur et information », vérifier que « Couleurs » conserve son menu et que « Onglets : OFF » se trouve juste dessous.
2. Ouvrir au moins trois vues (plan, 3D, feuille), puis cliquer sur « Onglets : OFF ». Vérifier le passage à ON et la bande au-dessus des onglets natifs : nom du document, noms des vues et miniatures, sans échelle ni métadonnées.
3. Sans cache existant, laisser Revit au repos : les images arrivent progressivement, sans changer la vue active. Une nomenclature conserve une vignette texte. Les caches existants sont réutilisés.
4. Cliquer sur chaque miniature : vérifier la vue activée, le contour bleu, et le défilement horizontal (molette ou barre) lorsque beaucoup de vues sont ouvertes.
5. Ouvrir, fermer et renommer une vue avec les commandes natives. La liste doit se mettre à jour en environ une seconde lorsque Revit est disponible.
6. Passer à un deuxième document : seules ses vues ouvertes doivent être affichées. Fermer tous les documents puis en ouvrir un autre : pas de bande orpheline.
7. Alterner WT/TW. La bande doit se reconstruire sans duplication. La V1 vise la fenêtre principale ; les fenêtres de vues flottantes ne reçoivent pas leur propre bande.
8. Faire ON/OFF dix fois. OFF doit rendre exactement l'espace à Revit et conserver les onglets et leur navigation. Les couleurs existantes doivent fonctionner indépendamment du bouton.
9. Tester la fermeture d'une vue/document ou OFF juste après un clic sur une miniature : aucune ancienne requête ne doit activer une vue d'un autre document.
10. Vérifier le thème sombre et l'affichage Windows à 100 % et 150 %. La bande utilise volontairement une palette claire lisible dans cette V1.

Limites volontaires : document actif uniquement, pas de changement interdocuments via la bande, pas de boutons de fermeture supplémentaires, ni favoris/sessions/badges. Les onglets natifs restent accessibles. Les exports d'image ne sont pas une vidéo temps réel ; un export individuel peut occuper Revit brièvement. Les vues non imprimables ou les exports refusés restent sans aperçu.

L'insertion vise le conteneur interne `DocumentPaneTabPanel` de Revit et doit être validée dans Revit réel. Une compilation et les tests WPF ne suffisent pas à certifier cette intégration.
