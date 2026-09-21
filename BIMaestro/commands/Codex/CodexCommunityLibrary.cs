using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace BIMaestro.Codex
{
    internal sealed class CodexCommunityLibrary : IDisposable
    {
        internal const long MaxBytes = 20000000;
        internal const long MaxPreviewBytes = 1000000;
        internal static readonly string CacheRoot = Path.Combine(CodexClient.DataDirectory, "CommunityFamilies");
        private readonly HttpClient http;
        private readonly string ownerPath;
        internal CodexCommunityLibrary()
        {
            string config = Path.Combine(CodexClient.DataDirectory, "community-library.json");
            string endpoint = File.Exists(config) ? (string)JObject.Parse(File.ReadAllText(config))["endpoint"] : "https://bimaestro-family-library.bimaestro-community.workers.dev";
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("La bibliothèque commune n'est pas encore configurée. Contactez l'administrateur BIMaestro.");
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(90), MaxResponseContentBufferSize = 1024 * 1024 };
            using (var sha = SHA256.Create()) ownerPath = Path.Combine(CodexClient.DataDirectory, "community-owner-" + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant()))).Replace("-", "") + ".bin");
        }
        private static async Task Check(HttpResponseMessage response)
        {
            if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
                throw new InvalidOperationException("Bibliothèque temporairement indisponible : un seuil de protection ou une limite d'utilisation a été atteint. Aucun nouvel essai automatique.");
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 403) throw new InvalidOperationException("Seul le profil Windows qui a publié cette famille peut la retirer. Les anciennes publications sans propriétaire ne peuvent pas être retirées ici.");
                if ((int)response.StatusCode == 409) throw new InvalidOperationException("Ce fichier a déjà été retiré de la bibliothèque et ne peut pas être republié à l'identique.");
                throw new InvalidOperationException("Le service de bibliothèque a refusé l'opération (HTTP " + (int)response.StatusCode + ").");
            }
            await Task.CompletedTask;
        }
        private void Identify(HttpRequestMessage request, bool create = false)
        {
            try
            {
                if (!File.Exists(ownerPath))
                {
                    if (!create) return;
                    Directory.CreateDirectory(CodexClient.DataDirectory);
                    byte[] secret = new byte[32]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
                    string temp = ownerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllBytes(temp, ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser)); try { File.Move(temp, ownerPath); } catch (IOException) { if (!File.Exists(ownerPath)) throw; } }
                    finally { Array.Clear(secret, 0, secret.Length); if (File.Exists(temp)) File.Delete(temp); }
                }
                byte[] token = ProtectedData.Unprotect(File.ReadAllBytes(ownerPath), null, DataProtectionScope.CurrentUser);
                if (token.Length != 32) throw new InvalidDataException();
                request.Headers.Add("X-Owner-Token", BitConverter.ToString(token).Replace("-", "").ToLowerInvariant()); Array.Clear(token, 0, token.Length);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException)
            { throw new InvalidOperationException("Impossible de lire ou conserver votre identité locale de publication. Aucun envoi effectué. Utilisez le même profil Windows et vérifiez les droits du dossier BIMaestro."); }
        }
        internal async Task<JObject> Search(string query, string version, string cursor = null, string category = "", string origin = "", bool mine = false)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, "v1/families?q=" + Uri.EscapeDataString(query) + "&revitVersion=" + Uri.EscapeDataString(version) + "&category=" + Uri.EscapeDataString(category) + "&origin=" + Uri.EscapeDataString(origin) + "&mine=" + (mine ? "1" : "0") + (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))))
            {
            Identify(request, mine);
            using (var response = await http.SendAsync(request))
            { await Check(response); return JObject.Parse(await response.Content.ReadAsStringAsync()); }
            }
        }
        internal async Task Publish(CodexFamilyArtifact artifact, string version)
        {
            await Publish(artifact.FilePath, Path.GetFileNameWithoutExtension(artifact.FilePath), (string)JObject.FromObject(artifact.Report)["category"] ?? "generic", "Famille créée avec BIMaestro Famille IA.", version, "ai", artifact.PreviewPath);
        }
        internal async Task<JObject> Publish(string path, string name, string category, string description, string version, string origin, string previewPath = null, string replacesId = null, string changeNote = null)
        {
            using (var file = CodexCommunitySnapshot.Read(path, MaxBytes))
            using (var request = new HttpRequestMessage(HttpMethod.Post, "v1/families"))
            {
                Identify(request, true);
                if (name.Length > 120) name = name.Substring(0, 120);
                request.Headers.Add("X-Family-Name", Uri.EscapeDataString(name));
                request.Headers.Add("X-Family-Category", Uri.EscapeDataString(category));
                request.Headers.Add("X-Revit-Version", version);
                request.Headers.Add("X-Family-Description", Uri.EscapeDataString(Regex.Replace(description ?? "", "[\\r\\n]+", " ")));
                request.Headers.Add("X-Family-Origin", origin);
                if (!string.IsNullOrWhiteSpace(replacesId)) request.Headers.Add("X-Replaces-Family-ID", replacesId);
                if (!string.IsNullOrWhiteSpace(changeNote)) request.Headers.Add("X-Change-Note", Uri.EscapeDataString(Regex.Replace(changeNote, "[\\r\\n]+", " ")));
                string creatorName = BIMaestro.Welcome.WelcomeManager.GetCommunityCreatorName();
                if (!string.IsNullOrWhiteSpace(creatorName)) request.Headers.Add("X-Creator-Name", Uri.EscapeDataString(creatorName));
                request.Content = new StreamContent(file);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentLength = file.Length;
                using (var response = await http.SendAsync(request))
                {
                    await Check(response);
                    var result = JObject.Parse(await response.Content.ReadAsStringAsync());
                    if (result["item"]?.Value<bool?>("isOwner") == true && result["item"]?.Value<bool?>("hasPreview") != true && !string.IsNullOrEmpty(previewPath) && File.Exists(previewPath))
                    {
                        try { await UploadPreview((string)result["item"]?["id"], previewPath); result["previewUploaded"] = true; }
                        catch (Exception ex) { result["previewUploaded"] = false; result["previewError"] = ex.Message; }
                    }
                    return result;
                }
            }
        }
        internal async Task<JArray> GetVersions(JObject item)
        {
            string id = (string)item?["id"];
            using (var request = new HttpRequestMessage(HttpMethod.Get, "v1/families/" + id + "/versions"))
            { Identify(request); using (var response = await http.SendAsync(request)) { await Check(response); return (JArray)JObject.Parse(await response.Content.ReadAsStringAsync())["items"] ?? new JArray(); } }
        }
        internal async Task EditMetadata(JObject item, string name, string category, string description, string origin)
        {
            using (var request = new HttpRequestMessage(new HttpMethod("PATCH"), "v1/families/" + (string)item["id"]))
            {
                Identify(request); request.Headers.Add("X-Family-Name", Uri.EscapeDataString(name)); request.Headers.Add("X-Family-Category", Uri.EscapeDataString(category));
                request.Headers.Add("X-Family-Description", Uri.EscapeDataString(Regex.Replace(description ?? "", "[\\r\\n]+", " "))); request.Headers.Add("X-Family-Origin", origin);
                using (var response = await http.SendAsync(request)) { await Check(response); var updated = (JObject)JObject.Parse(await response.Content.ReadAsStringAsync())["item"]; foreach (var property in updated.Properties()) item[property.Name] = property.Value; }
            }
        }
        private async Task UploadPreview(string id, string path)
        {
            if (!Regex.IsMatch(id ?? "", "^[a-fA-F0-9]{64}$")) return;
            string prepared = PreparePreview(path);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Put, "v1/families/" + id.ToLowerInvariant() + "/preview"))
                using (var file = File.OpenRead(prepared))
                {
                    Identify(request);
                    request.Content = new StreamContent(file);
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    request.Content.Headers.ContentLength = file.Length;
                    using (var response = await http.SendAsync(request)) await Check(response);
                }
            }
            finally { if (!string.Equals(prepared, path, StringComparison.OrdinalIgnoreCase)) try { File.Delete(prepared); } catch { } }
        }
        internal async Task UpdatePreview(JObject item, string path)
        {
            if (item?.Value<bool?>("isOwner") != true) throw new InvalidOperationException("Seul l’auteur peut modifier la photo de couverture.");
            await UploadPreview((string)item["id"], path);
            item["hasPreview"] = true;
            item["previewUrl"] = new Uri(PreviewUri(item).AbsoluteUri + "?v=" + DateTime.UtcNow.Ticks).AbsoluteUri;
        }
        private static string PreparePreview(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) throw new InvalidOperationException("Photo de couverture introuvable.");
            string folder = Path.Combine(CodexClient.DataDirectory, "CommunityPreviewDrafts"); Directory.CreateDirectory(folder);
            string output = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using (var source = System.Drawing.Image.FromFile(sourcePath))
                {
                    int box = Math.Min(1200, Math.Max(source.Width, source.Height));
                    while (box >= 240)
                    {
                        double scale = Math.Min(1d, (double)box / Math.Max(source.Width, source.Height));
                        int width = Math.Max(1, (int)Math.Round(source.Width * scale)), height = Math.Max(1, (int)Math.Round(source.Height * scale));
                        using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                        {
                            graphics.Clear(System.Drawing.Color.White);
                            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            graphics.DrawImage(source, 0, 0, width, height);
                            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        if (new FileInfo(output).Length <= MaxPreviewBytes) return output;
                        box = (int)(box * 0.75);
                    }
                }
            }
            catch (Exception ex) when (!(ex is InvalidOperationException)) { throw new InvalidOperationException("La photo choisie n’est pas une image valide.", ex); }
            try { File.Delete(output); } catch { }
            throw new InvalidOperationException("Impossible de compresser la photo sous 1 Mo.");
        }
        internal Uri PreviewUri(JObject item)
        {
            string id = (string)item?["id"];
            return item?.Value<bool?>("hasPreview") == true && Regex.IsMatch(id ?? "", "^[a-fA-F0-9]{64}$")
                ? new Uri(http.BaseAddress, "v1/families/" + id.ToLowerInvariant() + "/preview") : null;
        }
        internal async Task Remove(string id)
        {
            if (!Regex.IsMatch(id ?? "", "^[a-fA-F0-9]{64}$")) throw new InvalidOperationException("Identifiant invalide.");
            using (var request = new HttpRequestMessage(HttpMethod.Delete, "v1/families/" + id))
            { Identify(request); using (var response = await http.SendAsync(request)) await Check(response); }
        }
        internal async Task<string> Download(JObject item)
        {
            string id = (string)item["id"];
            if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[a-fA-F0-9]{64}$")) throw new InvalidOperationException("Identifiant de famille invalide.");
            string name = (string)item["name"] ?? "Famille";
            name = Regex.Replace(name, "[<>:\"/\\\\|?*\\x00-\\x1F]", "_").Trim().TrimEnd('.');
            if (name.Length > 100) name = name.Substring(0, 100).TrimEnd('.');
            if (string.IsNullOrWhiteSpace(name) || Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\\.|$)", RegexOptions.IgnoreCase)) name = "Famille_" + name;
            string directory = Path.Combine(CacheRoot, id.ToLowerInvariant());
            string destination = Path.Combine(directory, name + ".rfa");
            if (File.Exists(destination) && VerifyHash(destination, id)) return destination;
            // An existing modified file may be an edited user copy. Preserve it.
            if (File.Exists(destination)) destination = Path.Combine(directory, name + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".rfa");
            using (var response = await http.GetAsync("v1/families/" + Uri.EscapeDataString(id) + "/file", HttpCompletionOption.ResponseHeadersRead))
            {
                await Check(response);
                if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("Famille trop volumineuse.");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
                try
                {
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = File.Create(path))
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                    {
                        var buffer = new byte[81920]; long total = 0; int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) > 0)
                        {
                            total += count;
                            if (total > MaxBytes) throw new InvalidOperationException("Famille trop volumineuse.");
                            await output.WriteAsync(buffer, 0, count, timeout.Token);
                        }
                        if (total == 0 || (item.Value<long?>("sizeBytes") is long expected && expected != total)) throw new InvalidOperationException("Téléchargement incomplet.");
                    }
                    if (!VerifyHash(path, id)) throw new InvalidOperationException("Le contrôle d'intégrité du RFA a échoué. Fichier supprimé.");
                    File.Move(path, destination);
                    return destination;
                }
                catch { if (File.Exists(path)) File.Delete(path); throw; }
            }
        }
        private static bool VerifyHash(string path, string id)
        {
            if (new FileInfo(path).Length > MaxBytes) return false;
            using (var saved = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return string.Equals(BitConverter.ToString(sha.ComputeHash(saved)).Replace("-", ""), id, StringComparison.OrdinalIgnoreCase);
        }
        public void Dispose() { http.Dispose(); }
    }

}
