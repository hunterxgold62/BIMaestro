using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace BIMaestro.VideoGames
{
    internal abstract class GameMepBindableItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Raise([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    internal sealed class GameMepSystemItem : GameMepBindableItem
    {
        public GameMepSystemItem(GameMepSystemData data)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            ColorBrush = new SolidColorBrush(data.Color);
            try { ColorBrush.Freeze(); } catch { }
        }

        public GameMepSystemData Data { get; }
        public string Name => string.IsNullOrWhiteSpace(Data.Name)
            ? "Réseau sans nom"
            : Data.Name;
        public string Detail =>
            (string.IsNullOrWhiteSpace(Data.Classification)
                ? "Canalisation"
                : Data.Classification) +
            "  •  " + Data.ElementCount + " éléments";
        public Brush ColorBrush { get; }

        public bool IsEnabled
        {
            get => Data.IsVisible;
            set
            {
                if (Data.IsVisible == value)
                    return;
                Data.IsVisible = value;
                Raise();
            }
        }

        public void Refresh()
        {
            Raise(nameof(IsEnabled));
            Raise(nameof(Detail));
        }
    }

    internal sealed class GameMepSourceItem : GameMepBindableItem
    {
        public GameMepSourceItem(GameMepSourceData data, GameMepSystemData? system)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            SystemName = system?.Name ?? "Réseau non affecté";
        }

        public GameMepSourceData Data { get; }
        public string Name =>
            (Data.BoundaryKind == GameMepBoundaryKind.Inlet
                ? "Arrivée — "
                : "Retour — ") +
            (string.IsNullOrWhiteSpace(Data.Name) ? "sans nom" : Data.Name);
        public string SystemName { get; }
        public string ConfidenceText => Data.HasExplicitDirection
            ? "sens manuel"
            : "Confiance " + ToFrench(Data.Confidence);

        public bool IsActive
        {
            get => Data.IsActive;
            set
            {
                if (Data.IsActive == value)
                    return;
                Data.IsActive = value;
                Data.WasManuallyOverridden = true;
                Raise();
            }
        }

        public void Refresh()
        {
            Raise(nameof(IsActive));
        }

        private static string ToFrench(GameMepConfidence confidence)
        {
            switch (confidence)
            {
                case GameMepConfidence.High: return "élevée";
                case GameMepConfidence.Medium: return "moyenne";
                default: return "faible";
            }
        }
    }
}
