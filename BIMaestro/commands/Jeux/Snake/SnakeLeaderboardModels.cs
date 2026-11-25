using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using BIMaestro.Localization;

namespace BIMaestro.Bonus
{
    public class SnakeLeaderboardEntry
    {
        public string PlayerName { get; set; }
        public int Score { get; set; }
    }

    public class SnakeLeaderboardData
    {
        public List<SnakeLeaderboardEntry> Classic { get; set; } = new List<SnakeLeaderboardEntry>();
        public List<SnakeLeaderboardEntry> Arcade { get; set; } = new List<SnakeLeaderboardEntry>();
        public List<SnakeLeaderboardEntry> Hardcore { get; set; } = new List<SnakeLeaderboardEntry>();
        public List<SnakeLeaderboardEntry> Mode3D { get; set; } = new List<SnakeLeaderboardEntry>();
        public List<SnakeLeaderboardEntry> FlappyBird { get; set; } = new List<SnakeLeaderboardEntry>();
    }

    public class LeaderboardRow
    {
        public int Rank { get; set; }
        public string PlayerName { get; set; }
        public int Score { get; set; }
        public string ScoreText { get; set; }
        public bool IsMe { get; set; }
        public bool IsPlaceholder { get; set; }
    }

    // Un onglet = un mode de jeu, avec sa couleur.
    public class LeaderboardModeTab : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Key { get; set; }
        public string Label { get; set; }
        public Brush Accent { get; set; }
        public Brush AccentSoft { get; set; }
        public List<LeaderboardRow> Rows { get; set; } = new List<LeaderboardRow>();

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class SnakeLeaderboardViewModel : INotifyPropertyChanged
    {
        private LeaderboardModeTab _selected;

        public List<LeaderboardModeTab> Tabs { get; }

        public LeaderboardModeTab Selected
        {
            get => _selected;
            private set
            {
                _selected = value;
                Raise(nameof(Selected));
                Raise(nameof(First));
                Raise(nameof(Second));
                Raise(nameof(Third));
                Raise(nameof(Others));
                Raise(nameof(IsEmpty));
                Raise(nameof(HasOthers));
                Raise(nameof(MyRankText));
            }
        }

        public LeaderboardRow First => Podium(1);
        public LeaderboardRow Second => Podium(2);
        public LeaderboardRow Third => Podium(3);
        public List<LeaderboardRow> Others => _selected?.Rows.Skip(3).ToList() ?? new List<LeaderboardRow>();
        public bool IsEmpty => _selected == null || _selected.Rows.Count == 0;
        public bool HasOthers => Others.Count > 0;

        public string MyRankText
        {
            get
            {
                var me = _selected?.Rows.FirstOrDefault(r => r.IsMe);
                return me == null
                    ? UiLanguage.T("Tu n'es pas encore dans le top de ce mode.", "You're not in this mode's top yet.")
                    : UiLanguage.T("Ta place : ", "Your rank: ") + "#" + me.Rank + " • " + me.ScoreText + " pts";
            }
        }

        // Textes fixes (traduits ici plutôt que dans le XAML).
        public string TitleText => UiLanguage.T("Classement", "Leaderboard");
        public string SubtitleText => UiLanguage.T("Meilleurs scores de l'équipe BIMaestro", "Best scores of the BIMaestro team");
        public string EmptyTitle => UiLanguage.T("Aucun score pour ce mode.", "No score for this mode yet.");
        public string EmptyHint => UiLanguage.T("Sois le premier à inscrire ton nom !", "Be the first to set a score!");
        public string MeBadge => UiLanguage.T("Toi", "You");
        public string CloseHint => UiLanguage.T("fermer", "close");

        public SnakeLeaderboardViewModel(SnakeLeaderboardData data, string initialMode = null)
        {
            string me = Environment.UserName ?? "";

            Tabs = new List<LeaderboardModeTab>
            {
                Tab("classic", "Classic", "#3B82F6", data?.Classic, me),
                Tab("arcade", "Arcade", "#10B981", data?.Arcade, me),
                Tab("hardcore", "Hardcore", "#EF4444", data?.Hardcore, me),
                Tab("3d", "3D", "#8B5CF6", data?.Mode3D, me),
                Tab("flappy_bird", "Flappy Bird", "#F59E0B", data?.FlappyBird, me),
            };

            foreach (var t in Tabs)
                t.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(LeaderboardModeTab.IsSelected) && ((LeaderboardModeTab)s).IsSelected)
                        Selected = (LeaderboardModeTab)s;
                };

            var start = Tabs.FirstOrDefault(t => t.Key == initialMode) ?? Tabs[0];
            start.IsSelected = true;
            if (_selected == null) Selected = start;
        }

        private LeaderboardRow Podium(int rank)
        {
            var row = _selected?.Rows.FirstOrDefault(r => r.Rank == rank);
            return row ?? new LeaderboardRow { Rank = rank, PlayerName = "—", ScoreText = "", IsPlaceholder = true };
        }

        private static LeaderboardModeTab Tab(string key, string label, string hex, List<SnakeLeaderboardEntry> entries, string me)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var accent = new SolidColorBrush(color);
            var soft = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
            accent.Freeze();
            soft.Freeze();

            var rows = new List<LeaderboardRow>();
            int rank = 1;
            foreach (var e in (entries ?? new List<SnakeLeaderboardEntry>()).OrderByDescending(x => x.Score))
            {
                string name = string.IsNullOrWhiteSpace(e.PlayerName) ? UiLanguage.Get("SnakeLeaderboardModels.Player") : e.PlayerName;
                rows.Add(new LeaderboardRow
                {
                    Rank = rank++,
                    PlayerName = name,
                    Score = e.Score,
                    ScoreText = e.Score.ToString("N0", CultureInfo.CurrentCulture),
                    IsMe = me.Length > 0 && string.Equals(name, me, StringComparison.OrdinalIgnoreCase)
                });
            }

            return new LeaderboardModeTab { Key = key, Label = label, Accent = accent, AccentSoft = soft, Rows = rows };
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
