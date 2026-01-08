using System.IO;
using System.Text;
using System.Text.Json;

namespace BIMaestro.Welcome
{
    internal static class WelcomeStorage
    {
        private static readonly object _lock = new object();
        private static string FilePath => Path.Combine(Licensing.Paths.LicenseDir, "welcome_state.json");

        public static WelcomeState LoadOrCreate()
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Licensing.Paths.LicenseDir);

                if (!File.Exists(FilePath))
                {
                    var s = new WelcomeState { InstallId = Licensing.LicenseManager.GetOrCreateInstallId() };
                    Save(s);
                    return s;
                }

                try
                {
                    var json = File.ReadAllText(FilePath, Encoding.UTF8);
                    var s = JsonSerializer.Deserialize<WelcomeState>(json) ?? new WelcomeState();
                    if (string.IsNullOrWhiteSpace(s.InstallId))
                        s.InstallId = Licensing.LicenseManager.GetOrCreateInstallId();
                    return s;
                }
                catch
                {
                    var s = new WelcomeState { InstallId = Licensing.LicenseManager.GetOrCreateInstallId() };
                    Save(s);
                    return s;
                }
            }
        }

        public static void Save(WelcomeState state)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Licensing.Paths.LicenseDir);
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json, Encoding.UTF8);
            }
        }
    }
}
