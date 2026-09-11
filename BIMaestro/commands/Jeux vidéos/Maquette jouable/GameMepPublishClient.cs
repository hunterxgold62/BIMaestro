using System;
using System.IO;
using System.Linq;
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
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { MaxConnectionsPerServer = 8 })
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
            var assets = new[] { new GameMepWebAsset { Name = "index.zip", Bytes = package.Bytes, Sha256 = package.Sha256 } }.Concat(package.Assets).ToList();
            long totalBytes = assets.Sum(asset => asset.Size);
            if (assets.Count > 4096 || totalBytes > 512L * 1024 * 1024 || assets.Any(asset => asset.Size > 48L * 1024 * 1024))
                throw new InvalidOperationException("Export trop volumineux : limite de 512 Mo au total et 48 Mo par fichier. Réduisez la vue publiée.");
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
                packageBytes = totalBytes,
                assets = assets.Select(asset => new { name = asset.Name, bytes = asset.Size, sha256 = asset.Sha256 }),
                packageSha256 = package.Sha256,
                valveIds = package.ValveIds,
                manifest = JsonConvert.DeserializeObject(package.ManifestJson)
            };
            progress?.Report(new GameMepPublishProgress
                { Message = "Préparation du partage privé…", Percentage = 0.22 });
            dynamic start = await PostAsync(startBody, cancellationToken);
            int revision = (int)start.revision;
            string publicationId = (string)start.publicationId;
            string viewerToken = start.viewerToken == null ? "" : (string)start.viewerToken;
            string editorToken = start.editorToken == null ? "" : (string)start.editorToken;

            // Keep the returned tokens even if a later asset transfer is interrupted.
            state.PublicationId = publicationId;
            if (!string.IsNullOrWhiteSpace(viewerToken)) state.ViewerToken = viewerToken;
            if (!string.IsNullOrWhiteSpace(editorToken)) state.EditorToken = editorToken;
            Save(scene.MepGraph, state);
            long uploadedBytes = 0;
            int uploadedCount = 0;
            using (var slots = new SemaphoreSlim(8))
            {
                for (int offset = 0; offset < assets.Count; offset += 32)
                {
                    var batch = assets.Skip(offset).Take(32).ToArray();
                    dynamic destinations = await PostWithRetryAsync(new { action = "publish-asset", publicationId, revision, names = batch.Select(asset => asset.Name).ToArray() }, cancellationToken);
                    var urls = ((Newtonsoft.Json.Linq.JArray)destinations.uploads).ToDictionary(item => (string)item["name"], item => (string)item["uploadUrl"]);
                    await Task.WhenAll(batch.Select(async asset =>
                    {
                        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await UploadAssetAsync(asset, urls[asset.Name], cancellationToken).ConfigureAwait(false);
                            long sent = Interlocked.Add(ref uploadedBytes, asset.Size);
                            int count = Interlocked.Increment(ref uploadedCount);
                            progress?.Report(new GameMepPublishProgress { Message = "Envoi parallèle : " + count + " / " + assets.Count + " zones", Percentage = .25 + .6 * sent / totalBytes });
                        }
                        finally { slots.Release(); }
                    }));
                }
            }

            progress?.Report(new GameMepPublishProgress
                { Message = "Activation de la révision…", Percentage = 0.9 });
            await PostWithRetryAsync(new
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

        private static async Task UploadAssetAsync(GameMepWebAsset asset, string url, CancellationToken token)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Put, url))
                    {
                        request.Headers.TryAddWithoutValidation("x-upsert", "false");
                        request.Content = new ByteArrayContent(asset.Bytes);
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        using (var upload = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                        {
                            // A lost response may follow a successful PUT. Final verification
                            // checks every object against the expected size before activation.
                            if (upload.IsSuccessStatusCode || (attempt > 0 && (int)upload.StatusCode == 409)) return;
                            if ((int)upload.StatusCode >= 500 || (int)upload.StatusCode == 429 || (int)upload.StatusCode == 408)
                                throw new HttpRequestException("Transfert temporairement indisponible");
                            throw new InvalidOperationException("Transfert de zone refusé : " + await upload.Content.ReadAsStringAsync().ConfigureAwait(false));
                        }
                    }
                }
                catch (HttpRequestException) when (attempt < 2) { await Task.Delay(500 * (attempt + 1), token).ConfigureAwait(false); }
            }
        }

        private static async Task<dynamic> PostWithRetryAsync(object body, CancellationToken token)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return await PostAsync(body, token); }
                catch (HttpRequestException) when (attempt < 2) { await Task.Delay(700 * (attempt + 1), token); }
            }
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
                if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429 || (int)response.StatusCode == 408) throw new HttpRequestException(message);
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
