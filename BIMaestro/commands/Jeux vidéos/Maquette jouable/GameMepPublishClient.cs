using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace BIMaestro.VideoGames
{
    internal sealed class GameMepShareState
    {
        public string PublicationId { get; set; } = string.Empty;
        public string ViewerToken { get; set; } = string.Empty;
        public string EditorToken { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
        public int Revision { get; set; }
        public string ViewerUrl => string.IsNullOrWhiteSpace(ViewerToken)
            ? string.Empty
            : "https://viewer.bimaestro.fr/#/share/" + ViewerToken;
        public string EditorUrl => string.IsNullOrWhiteSpace(EditorToken)
            ? string.Empty
            : "https://viewer.bimaestro.fr/#/share/" + EditorToken;
    }

    internal sealed class GameMepPublishProgress
    {
        public string Message { get; set; } = string.Empty;
        public double Percentage { get; set; }
    }

    internal static class GameMepPublishClient
    {
        private const string FunctionUrl =
            "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/mep-share";
        private const string AnonKey =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inhxb3Z4ZmdnaGJxeHdzYWR6aHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTI0MDY5MzMsImV4cCI6MjA2Nzk4MjkzM30.ocKoeuUTLQ_oOr83TtpaJD3RUDOBbwLQ5nJNvOinYlo";
        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15)
        };

        public static async Task<GameMepShareState> PublishAsync(
            GameSceneData scene,
            string name,
            IProgress<GameMepPublishProgress> progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new GameMepPublishProgress
                { Message = "Création du paquet web…", Percentage = 0.08 });
            GameMepWebPackageResult package = await Task.Run(
                () => GameMepWebPackage.Build(scene, name), cancellationToken);
            if (package.Bytes.LongLength > 25L * 1024L * 1024L)
                throw new InvalidOperationException(
                    "Cette maquette dépasse la limite gratuite de 50 Mo. " +
                    "Réduisez la vue ou sa boîte de coupe avant de la partager.");
            GameMepShareState state = Load(scene.MepGraph);
            string modelKey = string.IsNullOrWhiteSpace(scene.MepGraph.ScenarioModelKey)
                ? scene.MepGraph.DocumentTitle
                : scene.MepGraph.ScenarioModelKey;
            var startBody = new
            {
                action = "publish-start",
                publicationId = string.IsNullOrWhiteSpace(state.PublicationId)
                    ? null
                    : state.PublicationId,
                name,
                modelKey,
                packageBytes = package.Bytes.LongLength,
                packageSha256 = package.Sha256,
                valveIds = package.ValveIds,
                manifest = JsonConvert.DeserializeObject(package.ManifestJson)
            };
            progress?.Report(new GameMepPublishProgress
                { Message = "Préparation du partage privé…", Percentage = 0.22 });
            dynamic start = await PostAsync(startBody, cancellationToken);
            string uploadUrl = (string)start.uploadUrl;
            int revision = (int)start.revision;
            string publicationId = (string)start.publicationId;
            string viewerToken = start.viewerToken == null ? "" : (string)start.viewerToken;
            string editorToken = start.editorToken == null ? "" : (string)start.editorToken;

            progress?.Report(new GameMepPublishProgress
                { Message = "Envoi de la maquette…", Percentage = 0.35 });
            using (var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl))
            {
                request.Headers.TryAddWithoutValidation("x-upsert", "false");
                request.Content = new ByteArrayContent(package.Bytes);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/zip");
                using (HttpResponseMessage upload = await Client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    string error = await upload.Content.ReadAsStringAsync();
                    if (!upload.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            "Transfert refusé (" + (int)upload.StatusCode + ") : " + error);
                }
            }

            progress?.Report(new GameMepPublishProgress
                { Message = "Activation de la révision…", Percentage = 0.9 });
            await PostAsync(new
            {
                action = "publish-complete",
                publicationId,
                revision
            }, cancellationToken);
            state.PublicationId = publicationId;
            state.Revision = revision;
            if (!string.IsNullOrWhiteSpace(viewerToken)) state.ViewerToken = viewerToken;
            if (!string.IsNullOrWhiteSpace(editorToken)) state.EditorToken = editorToken;
            DateTime expires;
            state.ExpiresAtUtc = DateTime.TryParse((string)start.expiresAt, out expires)
                ? expires.ToUniversalTime()
                : DateTime.UtcNow.AddDays(30);
            Save(scene.MepGraph, state);
            progress?.Report(new GameMepPublishProgress
                { Message = "Partage prêt", Percentage = 1.0 });
            return state;
        }

        public static async Task ExtendAsync(
            GameMepGraphData graph,
            GameMepShareState state,
            int days,
            CancellationToken cancellationToken)
        {
            dynamic result = await PostAsync(new
            {
                action = "manage",
                command = "extend",
                publicationId = state.PublicationId,
                days
            }, cancellationToken);
            state.ExpiresAtUtc = DateTime.Parse((string)result.expiresAt).ToUniversalTime();
            Save(graph, state);
        }

        public static async Task RevokeAsync(
            GameMepGraphData graph,
            GameMepShareState state,
            CancellationToken cancellationToken)
        {
            await PostAsync(new
            {
                action = "manage",
                command = "revoke",
                publicationId = state.PublicationId
            }, cancellationToken);
            Delete(graph);
        }

        public static GameMepShareState Load(GameMepGraphData graph)
        {
            try
            {
                string path = StatePath(graph);
                return File.Exists(path)
                    ? JsonConvert.DeserializeObject<GameMepShareState>(File.ReadAllText(path)) ??
                        new GameMepShareState()
                    : new GameMepShareState();
            }
            catch { return new GameMepShareState(); }
        }

        private static async Task<dynamic> PostAsync(
            object body,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, FunctionUrl);
            request.Headers.Add("apikey", AnonKey);
            string jwt = global::BIMaestroApp.LicenseJwt;
            if (!string.IsNullOrWhiteSpace(jwt))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Content = new StringContent(
                JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
            string json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                dynamic failure = JsonConvert.DeserializeObject(json);
                string message = failure?.error == null ? json : (string)failure.error;
                throw new InvalidOperationException(message);
            }
            return JsonConvert.DeserializeObject(json) ??
                throw new InvalidOperationException("Réponse de publication vide.");
        }

        private static void Save(GameMepGraphData graph, GameMepShareState state)
        {
            string path = StatePath(graph);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        private static void Delete(GameMepGraphData graph)
        {
            try { File.Delete(StatePath(graph)); } catch { }
        }

        private static string StatePath(GameMepGraphData graph)
        {
            string key = string.IsNullOrWhiteSpace(graph.ScenarioModelKey)
                ? graph.DocumentTitle
                : graph.ScenarioModelKey;
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? "model"));
            string hash = BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIMaestro", "MepShares", hash + ".json");
        }
    }
}
