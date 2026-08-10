using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        public List<SnakeLeaderboardEntry> FlappyBird { get; set; } = new List<SnakeLeaderboardEntry>();
    }

    public class LeaderboardRow
    {
        public int Rank { get; set; }
        public string PlayerName { get; set; }
        public int Score { get; set; }
    }

    public class SnakeLeaderboardViewModel
    {
        public ObservableCollection<LeaderboardRow> ClassicEntries { get; }
        public ObservableCollection<LeaderboardRow> ArcadeEntries { get; }
        public ObservableCollection<LeaderboardRow> HardcoreEntries { get; }
        public ObservableCollection<LeaderboardRow> FlappyBirdEntries { get; }

        public SnakeLeaderboardViewModel(SnakeLeaderboardData data)
        {
            ClassicEntries = BuildRows(data?.Classic);
            ArcadeEntries = BuildRows(data?.Arcade);
            HardcoreEntries = BuildRows(data?.Hardcore);
            FlappyBirdEntries = BuildRows(data?.FlappyBird);
        }

        private static ObservableCollection<LeaderboardRow> BuildRows(List<SnakeLeaderboardEntry> entries)
        {
            var rows = new ObservableCollection<LeaderboardRow>();
            if (entries == null) return rows;

            int rank = 1;
            foreach (var entry in entries)
            {
                rows.Add(new LeaderboardRow
                {
                    Rank = rank,
                    PlayerName = string.IsNullOrWhiteSpace(entry.PlayerName) ? UiLanguage.T("Joueur", "Player") : entry.PlayerName,
                    Score = entry.Score
                });
                rank++;
            }

            if (rows.Count == 0)
            {
                rows.Add(new LeaderboardRow
                {
                    Rank = 1,
                    PlayerName = UiLanguage.T("Aucun score", "No Score"),
                    Score = 0
                });
            }

            return rows;
        }
    }
}
