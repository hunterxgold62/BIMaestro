using System;
using System.Globalization;
using Autodesk.Revit.UI;

namespace BIMaestro.VideoGames
{
    /// <summary>
    /// Point d'entrée unique pour les différences d'API de la maquette BIM.
    /// La DLL officielle reste compilée avec Revit 2023 ; les adaptations
    /// nécessaires aux versions suivantes doivent être isolées ici ou dans
    /// des appels par réflexion afin de ne jamais lier le binaire à Revit 2024+.
    /// </summary>
    internal sealed class RevitGameCompatibility
    {
        private RevitGameCompatibility(int majorVersion, string versionName)
        {
            MajorVersion = majorVersion;
            VersionName = versionName ?? string.Empty;
        }

        public int MajorVersion { get; }
        public string VersionName { get; }
        public bool IsValidatedVersion => MajorVersion >= 2023 && MajorVersion <= 2025;
        public bool UsesLongElementIds => MajorVersion >= 2024;

        public static RevitGameCompatibility Detect(UIApplication application)
        {
            string number = string.Empty;
            string name = string.Empty;
            try { number = application?.Application?.VersionNumber ?? string.Empty; }
            catch { }
            try { name = application?.Application?.VersionName ?? string.Empty; }
            catch { }

            int majorVersion = 0;
            int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out majorVersion);
            if (string.IsNullOrWhiteSpace(name))
                name = majorVersion > 0 ? "Revit " + majorVersion : "Revit inconnu";

            var compatibility = new RevitGameCompatibility(majorVersion, name);
            GameRuntimeDiagnostics.ConfigureRevitVersion(majorVersion);
            GameRuntimeDiagnostics.Write(
                "Compatibilité Maquette BIM : " + compatibility.VersionName +
                " | DLL cible API 2023" +
                " | ElementId " +
                (compatibility.UsesLongElementIds ? "64 bits" : "32 bits") +
                (compatibility.IsValidatedVersion ? string.Empty :
                    " | version non validée"));
            return compatibility;
        }
    }
}
