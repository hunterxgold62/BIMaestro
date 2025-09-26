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
        /// Case à cocher du parent :
        /// - Propagation dans les deux sens (parent -> enfants).
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;

                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));

                // Propagation vers les enfants
                if (SubFamilies != null)
                {
                    foreach (var child in SubFamilies)
                        child.IsSelected = value;
                }

                // Si tu relies l'affichage des sous-familles à IsSelected
                OnPropertyChanged(nameof(VisibleSubFamilies));
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
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}
