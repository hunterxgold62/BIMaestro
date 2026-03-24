using Licensing;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace BIMaestro.Bonus
{
    internal static class SnakeLeaderboardClient
    {
        private static readonly string[] Paths = { "functions/v1/snake-leaderboard", "snake-leaderboard" };

        public static async Task<SnakeLeaderboardData> FetchLeaderboardAsync(string baseUrl, string jwt)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));
            if (string.IsNullOrWhiteSpace(jwt)) throw new ArgumentNullException(nameof(jwt));

            using var client = NetSupport.CreateHttpClient(TimeSpan.FromSeconds(10));
            var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");

            for (int i = 0; i < Paths.Length; i++)
            {
                var endpoint = new Uri(baseUri, Paths[i]);
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Add("Authorization", $"Bearer {jwt}");

                using var response = await client.SendAsync(request).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                    || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                {
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseLeaderboard(payload);
            }

            throw new InvalidOperationException("Leaderboard endpoint not available.");
        }

        public static async Task SubmitRecordAsync(string baseUrl, string jwt, string mode, int score, string playerName, string installId)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));
            if (string.IsNullOrWhiteSpace(jwt)) throw new ArgumentNullException(nameof(jwt));

            var payload = new
            {
                mode = mode,
                score = Math.Max(0, score),
                player_name = playerName,
                install_id = installId
            };

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);

            using var client = NetSupport.CreateHttpClient(TimeSpan.FromSeconds(10));
            var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");

            for (int i = 0; i < Paths.Length; i++)
            {
                var endpoint = new Uri(baseUri, Paths[i]);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Add("Authorization", $"Bearer {jwt}");
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                    || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                {
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return;
            }
        }

        private static SnakeLeaderboardData ParseLeaderboard(string json)
        {
            var data = new SnakeLeaderboardData();
            if (string.IsNullOrWhiteSpace(json)) return data;

            var root = JObject.Parse(json);
            data.Classic = ParseEntries(root["classic"]);
            data.Arcade = ParseEntries(root["arcade"]);
            data.Hardcore = ParseEntries(root["hardcore"]);
            data.FlappyBird = ParseEntries(root["flappy_bird"] ?? root["flappyBird"] ?? root["flappy-bird"]);
            return data;
        }

        private static List<SnakeLeaderboardEntry> ParseEntries(JToken token)
        {
            var list = new List<SnakeLeaderboardEntry>();
            if (token == null) return list;

            foreach (var item in token)
            {
                list.Add(new SnakeLeaderboardEntry
                {
                    PlayerName = item.Value<string>("player_name"),
                    Score = item.Value<int?>("score") ?? 0
                });
            }

            return list;
        }
    }
}
