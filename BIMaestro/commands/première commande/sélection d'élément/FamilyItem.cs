using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Visualisation
{
    public class FamilyItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Name { get; set; }

        /// <summary>
        /// Sous-familles (types) liées à cette famille.
        /// </summary>
        public ObservableCollection<FamilyItem> SubFamilies { get; set; }

        /// <summary>
        /// Propriété principale pour (dé)cocher.
        /// - Si on décoche le parent, on force la décoche de tous les enfants
        ///   pour éviter les incohérences.
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                    OnPropertyChanged(nameof(VisibleSubFamilies));

                    // Parent décoché ⇒ forcer la décoche de tous les enfants
                    if (!_isSelected && SubFamilies != null)
                    {
                        foreach (var child in SubFamilies)
                            child.IsSelected = false;
                    }
                }
            }
        }

        /// <summary>
        /// Si le parent n'est pas coché, on n'affiche pas ses sous-familles (pour alléger l'UI).
        /// </summary>
        public ObservableCollection<FamilyItem> VisibleSubFamilies
        {
            get
            {
                if (IsSelected)
                    return SubFamilies;
                else
                    return new ObservableCollection<FamilyItem>();
            }
        }

        public FamilyItem()
        {
            SubFamilies = new ObservableCollection<FamilyItem>();
            IsSelected = true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
