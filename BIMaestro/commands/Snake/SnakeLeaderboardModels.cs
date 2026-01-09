using System.Collections.Generic;
using System.Collections.ObjectModel;

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

        public SnakeLeaderboardViewModel(SnakeLeaderboardData data)
        {
            ClassicEntries = BuildRows(data?.Classic);
            ArcadeEntries = BuildRows(data?.Arcade);
            HardcoreEntries = BuildRows(data?.Hardcore);
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
                    PlayerName = string.IsNullOrWhiteSpace(entry.PlayerName) ? "Joueur" : entry.PlayerName,
                    Score = entry.Score
                });
                rank++;
            }

            if (rows.Count == 0)
            {
                rows.Add(new LeaderboardRow
                {
                    Rank = 1,
                    PlayerName = "Aucun score",
                    Score = 0
                });
            }

            return rows;
        }
    }
}