using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Page
{
    internal sealed class UpdateManifest
    {
        [JsonProperty("version")]
        public string VersionText { get; set; }

        [JsonProperty("downloadUrl")]
        public string DownloadUrl { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonIgnore]
        public Version Version { get; set; }
    }

    internal sealed class UpdateInstallResult
    {
        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("exitCode")]
        public int ExitCode { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("timestampUtc")]
        public DateTime TimestampUtc { get; set; }
    }

    internal static class DirectUpdateService
    {
        // Ce fichier JSON doit etre publie avec les nouvelles versions.
        internal const string ManifestUrl = "https://www.bimaestro.fr/update.json";
        private const long MaximumInstallerBytes = 500L * 1024L * 1024L;
        private static readonly TimeSpan DownloadLockTimeout = TimeSpan.FromMinutes(12);

        internal static async Task<UpdateManifest> FetchManifestAsync(CancellationToken cancellationToken = default)
        {
            using (var http = CreateHttpClient(TimeSpan.FromSeconds(10)))
            using (var response = await http.GetAsync(ManifestUrl, HttpCompletionOption.ResponseContentRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync();
                var manifest = JsonConvert.DeserializeObject<UpdateManifest>(json);
                ValidateManifest(manifest);
                return manifest;
            }
        }

        internal static async Task DownloadAndScheduleAsync(
            UpdateManifest manifest,
            IProgress<int> progress,
            CancellationToken cancellationToken = default)
        {
            ValidateManifest(manifest);

            using (FileStream downloadLock = await AcquireDownloadLockAsync(cancellationToken))
            {
                await DownloadAndScheduleUnderLockAsync(manifest, progress, cancellationToken);
            }
            TryDeleteFile(DownloadLockPath);
        }

        internal static UpdateInstallResult LoadLastInstallResult()
        {
            try
            {
                if (!File.Exists(InstallResultPath)) return null;
                string json = File.ReadAllText(InstallResultPath);
                return JsonConvert.DeserializeObject<UpdateInstallResult>(json);
            }
            catch
            {
                return null;
            }
        }

        internal static void DeleteLastInstallResult()
        {
            try { if (File.Exists(InstallResultPath)) File.Delete(InstallResultPath); }
            catch { }
        }

        private static async Task DownloadAndScheduleUnderLockAsync(
            UpdateManifest manifest,
            IProgress<int> progress,
            CancellationToken cancellationToken)
        {

            string updateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIMaestro",
                "Updates",
                manifest.Version.ToString());
            Directory.CreateDirectory(updateDirectory);

            string installerPath = Path.Combine(updateDirectory, "BIMaestroInstaller.exe");
            string partialPath = installerPath + ".download";
            string scheduledPath = Path.Combine(updateDirectory, "install.scheduled");

            try
            {
                if (File.Exists(installerPath))
                {
                    try
                    {
                        VerifySha256(installerPath, manifest.Sha256);
                        progress?.Report(100);
                        if (!IsUpdaterStillScheduled(scheduledPath))
                            ScheduleUpdater(installerPath, scheduledPath);
                        return;
                    }
                    catch (InvalidDataException)
                    {
                        TryDeleteFile(installerPath);
                        TryDeleteFile(scheduledPath);
                    }
                }

                using (var http = CreateHttpClient(TimeSpan.FromMinutes(10)))
                using (var response = await http.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    long? total = response.Content.Headers.ContentLength;
                    if (total.HasValue && total.Value > MaximumInstallerBytes)
                        throw new InvalidDataException("Le fichier de mise a jour est anormalement volumineux.");

                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long received = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            received += read;
                            if (received > MaximumInstallerBytes)
                                throw new InvalidDataException("Le fichier de mise a jour est anormalement volumineux.");

                            await output.WriteAsync(buffer, 0, read, cancellationToken);
                            if (total.GetValueOrDefault() > 0)
                                progress?.Report((int)Math.Min(100, received * 100L / total.Value));
                        }
                    }
                }

                VerifySha256(partialPath, manifest.Sha256);
                if (File.Exists(installerPath)) File.Delete(installerPath);
                File.Move(partialPath, installerPath);
                progress?.Report(100);
                ScheduleUpdater(installerPath, scheduledPath);
            }
            finally
            {
                TryDeleteFile(partialPath);
            }
        }

        private static string InstallResultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BIMaestro",
            "Updates",
            "last-result.json");

        private static string DownloadLockPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BIMaestro",
            "Updates",
            "download.lock");

        private static async Task<FileStream> AcquireDownloadLockAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DownloadLockPath));
            DateTime deadline = DateTime.UtcNow + DownloadLockTimeout;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return new FileStream(
                        DownloadLockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (IOException)
                {
                    await Task.Delay(250, cancellationToken);
                }
            }

            throw new TimeoutException("Une autre instance de Revit telecharge deja la mise a jour.");
        }

        private static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BIMaestro-Updater/" + (BIMaestroApp.PluginVersion ?? "unknown"));
            return http;
        }

        private static void ValidateManifest(UpdateManifest manifest)
        {
            if (manifest == null || !Version.TryParse(manifest.VersionText, out Version version))
                throw new InvalidDataException("Le manifeste de mise a jour ne contient pas de version valide.");

            if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out Uri downloadUri)
                || !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("L'adresse de telechargement doit utiliser HTTPS.");

            string normalizedHash = NormalizeHash(manifest.Sha256);
            if (normalizedHash.Length != 64)
                throw new InvalidDataException("L'empreinte SHA-256 de la mise a jour est absente ou invalide.");

            manifest.Version = version;
            manifest.Sha256 = normalizedHash;
        }

        private static void VerifySha256(string path, string expectedHash)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                if (!string.Equals(actual, NormalizeHash(expectedHash), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("La verification de securite SHA-256 de la mise a jour a echoue.");
            }
        }

        private static string NormalizeHash(string value) =>
            (value ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).Trim();

        private static void ScheduleUpdater(string installerPath, string scheduledPath)
        {
            TryDeleteFile(scheduledPath);
            Process updater = StartUpdater(installerPath);
            try
            {
                File.WriteAllText(scheduledPath, updater.Id.ToString());
            }
            catch
            {
                // L'assistant est deja demarre. L'absence du marqueur ne doit pas annuler l'installation.
            }
        }

        private static bool IsUpdaterStillScheduled(string scheduledPath)
        {
            try
            {
                if (!File.Exists(scheduledPath)) return false;
                if (!int.TryParse(File.ReadAllText(scheduledPath).Trim(), out int processId))
                {
                    TryDeleteFile(scheduledPath);
                    return false;
                }

                using (var process = Process.GetProcessById(processId))
                    return !process.HasExited;
            }
            catch
            {
                TryDeleteFile(scheduledPath);
                return false;
            }
        }

        private static Process StartUpdater(string installerPath)
        {
            string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string bundledUpdater = Path.Combine(assemblyDirectory, "BIMaestro.Updater.exe");
            if (!File.Exists(bundledUpdater))
                throw new FileNotFoundException("Le composant BIMaestro.Updater.exe est introuvable.", bundledUpdater);

            string temporaryUpdater = Path.Combine(Path.GetTempPath(), "BIMaestro.Updater." + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(bundledUpdater, temporaryUpdater, true);

            var startInfo = new ProcessStartInfo
            {
                FileName = temporaryUpdater,
                Arguments = Quote(installerPath) + " " + Process.GetCurrentProcess().Id,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Impossible de demarrer l'assistant de mise a jour.");
            return process;
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
