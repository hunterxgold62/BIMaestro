using Newtonsoft.Json;
using System;
using System.IO;
using System.Text;

namespace BIMaestro.Welcome
{
    internal static class WelcomeStorage
    {
        private static readonly object _lock = new object();

        // On stocke au même endroit que ta licence (cohérent et déjà créé)
        private static string FilePath =>
            Path.Combine(Licensing.Paths.LicenseDir, "welcome_state.json");

        public static WelcomeState LoadOrCreate()
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Licensing.Paths.LicenseDir);

                if (!File.Exists(FilePath))
                {
                    var s = new WelcomeState
                    {
                        InstallId = Licensing.LicenseManager.GetOrCreateInstallId()
                    };
                    Save(s);
                    return s;
                }

                try
                {
                    var json = File.ReadAllText(FilePath, Encoding.UTF8);
                    var s = JsonConvert.DeserializeObject<WelcomeState>(json) ?? new WelcomeState();

                    if (string.IsNullOrWhiteSpace(s.InstallId))
                        s.InstallId = Licensing.LicenseManager.GetOrCreateInstallId();

                    return s;
                }
                catch
                {
                    // Si fichier corrompu -> reset sans casser Revit
                    var s = new WelcomeState
                    {
                        InstallId = Licensing.LicenseManager.GetOrCreateInstallId()
                    };
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

                try
                {
                    var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                    File.WriteAllText(FilePath, json, Encoding.UTF8);
                }
                catch
                {
                    // Jamais bloquer Revit pour un fichier de settings
                }
            }
        }
    }
}
