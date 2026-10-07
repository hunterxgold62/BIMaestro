; Included for each Revit binary folder: families must sit beside its DLL.
#if !FileExists(TrainingBin + "\Demo\Maquette\BIMaestro_Apprentissage_2024.rvt")
  #error Maquette de reference Revit 2024 absente. Generez la reference puis recompilez les binaires avec les ressources Demo.
#endif
#if !FileExists(TrainingBin + "\Demo\Maquette\Familles\CML_Réservation rectangulaire murale.rfa") || !FileExists(TrainingBin + "\Demo\Maquette\Familles\CML_Parking.rfa") || !FileExists(TrainingBin + "\Demo\Maquette\Familles\CML_Table ronde + chaise.rfa")
  #error Maquette de formation incomplete : reservation, parking ou mobilier absent. Recompilez le dossier binaire concerne avec ses ressources Demo.
#endif
#if !FileExists(TrainingBin + "\Demo\Maquette\Familles\Coude - Générique.rfa") || !FileExists(TrainingBin + "\Demo\Maquette\Familles\Coude rectangulaire - En onglet.rfa")
  #error Maquette de formation incomplete : coude de canalisation ou de gaine absent.
#endif
#if !FileExists(TrainingBin + "\Demo\Maquette\Familles\Vanne papillon - 50-300 mm.rfa") || !FileExists(TrainingBin + "\Demo\Maquette\Familles\Filtre à tamis en Y - 50-500 mm - A brides.rfa") || !FileExists(TrainingBin + "\Demo\Maquette\Familles\CML_Compteur d'eau à brides DN40-150.rfa")
  #error Maquette de formation incomplete : vanne, filtre ou compteur MEP Booster absent.
#endif
#if !FileExists(TrainingBin + "\Demo\NavigateurFamilles\Images\Mobilier\Bureau\Bureau commun.png") || !FileExists(TrainingBin + "\Demo\NavigateurFamilles\Familles\Mobilier\Bureau\Bureau commun.rfa")
  #error Catalogue de formation absent du dossier binaire concerne. Recompilez avec les ressources Demo.
#endif
