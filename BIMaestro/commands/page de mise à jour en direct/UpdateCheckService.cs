using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Page
{
    internal sealed class UpdateManifest
    {
        [JsonProperty("version")]
        public string VersionText { get; set; }

        [JsonIgnore]
        public Version Version { get; set; }
    }

    // Notification uniquement : aucun telechargement ou lancement d'installateur.
    internal static class UpdateCheckService
    {
        internal const string ManifestUrl = "https://www.bimaestro.fr/update.json";
        internal const string DownloadPageUrl = "https://www.bimaestro.fr/telechargement";

        internal static async Task<UpdateManifest> FetchManifestAsync(CancellationToken cancellationToken = default)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BIMaestro-UpdateCheck/" + (BIMaestroApp.PluginVersion ?? "unknown"));
                using (var response = await http.GetAsync(ManifestUrl, HttpCompletionOption.ResponseContentRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync();
                    var manifest = JsonConvert.DeserializeObject<UpdateManifest>(json);
                    if (manifest == null || !Version.TryParse(manifest.VersionText, out Version version))
                        throw new InvalidDataException("Le manifeste de mise a jour ne contient pas de version valide.");

                    manifest.Version = version;
                    return manifest;
                }
            }
        }

        internal static void OpenDownloadPage()
        {
            // URL officielle fixe : ne jamais executer une adresse issue du manifeste.
            Process.Start(new ProcessStartInfo(DownloadPageUrl) { UseShellExecute = true });
        }
    }
}
