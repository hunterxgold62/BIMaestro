using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Modification
{
    [DataContract]
    public enum VerticalPlacementMode
    {
        [EnumMember] Auto = 0,
        [EnumMember] Center = 1,
        [EnumMember] Bottom = 2,
        [EnumMember] Top = 3
    }

    [DataContract]
    public class ReservationAutoV3Config
    {
        [DataMember] public ProfileConfig WallRect { get; set; } = ProfileConfig.DefaultWallRect();
        [DataMember] public ProfileConfig WallCirc { get; set; } = ProfileConfig.DefaultWallCirc();
        [DataMember] public ProfileConfig FloorRect { get; set; } = ProfileConfig.DefaultFloorRect();
        [DataMember] public ProfileConfig FloorCirc { get; set; } = ProfileConfig.DefaultFloorCirc();

        // Ancien réglage conservé pour relire les fichiers de configuration existants.
        [DataMember] public double OversizeMm_PipeDuct { get; set; } = 0.0;

        // Profondeur : pas d'oversize
        [DataMember] public double OversizeMm_Depth { get; set; } = 0.0;

        [DataMember] public bool DefaultNormeEnabled { get; set; } = true;
        [DataMember] public bool DefaultDynamoAutoEnabled { get; set; } = true;

        [DataMember] public string LastHostTarget { get; set; } = "Mur";
        [DataMember] public string LastShapeTarget { get; set; } = "Rectangulaire";
        [DataMember] public string LastShapeOptionLabel { get; set; } = "";
        [DataMember] public string LastObjectType { get; set; } = "Canalisation";
        [DataMember] public string LastPipeSource { get; set; } = "Maquette";
        [DataMember] public bool LastAutomaticEnabled { get; set; }
        [DataMember] public bool LastDoubleLinkEnabled { get; set; }
        [DataMember] public bool LastMultiEnabled { get; set; }

        [DataMember]
        public string DynamoPath { get; set; } =
            @"P:\0-Boîte à outils Revit\1-Dynamo\CML_Arases réservations_par niveau_V24.dyn";

        [DataMember] public string LastRfaPath { get; set; } = "";
    }

    [DataContract]
    public class ProfileConfig
    {
        [DataMember] public string FamilyName { get; set; } = "";
        [DataMember] public string TypeName { get; set; } = "";

        [DataMember] public string ParamLength { get; set; } = "";
        [DataMember] public string ParamWidth { get; set; } = "";
        [DataMember] public string ParamHeight { get; set; } = "";
        [DataMember] public string ParamDiameter { get; set; } = "";
        [DataMember] public string ParamDepth { get; set; } = "";

        // Option B : auto + correction utilisateur
        [DataMember] public VerticalPlacementMode VerticalPlacementMode { get; set; } = VerticalPlacementMode.Auto;
        [DataMember] public double VerticalPlacementOffsetMm { get; set; } = 0.0;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(FamilyName);

        public static ProfileConfig DefaultWallRect() => new ProfileConfig
        {
            FamilyName = "CML_Réservation rectangulaire verticale",
            TypeName = "",
            ParamLength = "Longueur",
            ParamHeight = "Hauteur",
            ParamDepth = "Profondeur",
            VerticalPlacementMode = VerticalPlacementMode.Auto,
            VerticalPlacementOffsetMm = 0.0
        };

        public static ProfileConfig DefaultFloorRect() => new ProfileConfig
        {
            FamilyName = "CML_Réservation rectangulaire horizontale",
            TypeName = "",
            ParamLength = "Longueur",
            ParamWidth = "Largeur",
            ParamDepth = "Profondeur",
            VerticalPlacementMode = VerticalPlacementMode.Auto,
            VerticalPlacementOffsetMm = 0.0
        };

        public static ProfileConfig DefaultWallCirc() => new ProfileConfig
        {
            FamilyName = "CML_Réservation circulaire verticale",
            TypeName = "",
            ParamDiameter = "Diamètre",
            ParamDepth = "Profondeur",
            VerticalPlacementMode = VerticalPlacementMode.Auto,
            VerticalPlacementOffsetMm = 0.0
        };

        public static ProfileConfig DefaultFloorCirc() => new ProfileConfig
        {
            FamilyName = "CML_Réservation circulaire horizontale",
            TypeName = "",
            ParamDiameter = "Diamètre",
            ParamDepth = "Profondeur",
            VerticalPlacementMode = VerticalPlacementMode.Auto,
            VerticalPlacementOffsetMm = 0.0
        };
    }

    public static class ReservationAutoV3ConfigStore
    {
        private static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMaestro");

        public static string ConfigPath =>
            Path.Combine(Folder, "ReservationAutoV3.json");

        public static ReservationAutoV3Config LoadOrDefault()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return new ReservationAutoV3Config();

                using (var fs = File.OpenRead(ConfigPath))
                {
                    var ser = new DataContractJsonSerializer(typeof(ReservationAutoV3Config));
                    var cfg = ser.ReadObject(fs) as ReservationAutoV3Config;
                    return cfg ?? new ReservationAutoV3Config();
                }
            }
            catch
            {
                return new ReservationAutoV3Config();
            }
        }

        public static bool Save(ReservationAutoV3Config cfg, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(Folder);

                using (var fs = File.Create(ConfigPath))
                {
                    var ser = new DataContractJsonSerializer(typeof(ReservationAutoV3Config));
                    ser.WriteObject(fs, cfg);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    [DataContract]
    public class ReservationAutoV3PersoConfig
    {
        [DataMember] public ProfileConfig WallRectPerso { get; set; } = new ProfileConfig();
        [DataMember] public ProfileConfig WallCircPerso { get; set; } = new ProfileConfig();
        [DataMember] public ProfileConfig FloorRectPerso { get; set; } = new ProfileConfig();
        [DataMember] public ProfileConfig FloorCircPerso { get; set; } = new ProfileConfig();

        [DataMember] public ProfileConfig WallRectHosted { get; set; } = new ProfileConfig();
        [DataMember] public ProfileConfig WallRectUnhosted { get; set; } = new ProfileConfig();
        [DataMember] public ProfileConfig WallCircHosted { get; set; } = new ProfileConfig();
        [DataMember] public ProfileConfig WallCircUnhosted { get; set; } = new ProfileConfig();
        [DataMember] public ProfileConfig FloorRectHosted { get; set; } = new ProfileConfig();
        [DataMember] public ProfileConfig FloorRectUnhosted { get; set; } = new ProfileConfig();
        [DataMember] public ProfileConfig FloorCircHosted { get; set; } = new ProfileConfig();
        [DataMember] public ProfileConfig FloorCircUnhosted { get; set; } = new ProfileConfig();

        public ProfileConfig Get(ReservationAutoV3Window.HostTarget host, ReservationAutoV3Window.ShapeTarget shape)
        {
            return (host, shape) switch
            {
                (ReservationAutoV3Window.HostTarget.Mur, ReservationAutoV3Window.ShapeTarget.Rectangulaire) => WallRectPerso,
                (ReservationAutoV3Window.HostTarget.Mur, ReservationAutoV3Window.ShapeTarget.Circulaire) => WallCircPerso,
                (ReservationAutoV3Window.HostTarget.Sol, ReservationAutoV3Window.ShapeTarget.Rectangulaire) => FloorRectPerso,
                (ReservationAutoV3Window.HostTarget.Sol, ReservationAutoV3Window.ShapeTarget.Circulaire) => FloorCircPerso,
                _ => null
            };
        }

        public ProfileConfig Get(
            ReservationAutoV3Window.HostTarget host,
            ReservationAutoV3Window.ShapeTarget shape,
            bool unhosted)
        {
            EnsureInitialized();

            return (host, shape, unhosted) switch
            {
                (ReservationAutoV3Window.HostTarget.Mur, ReservationAutoV3Window.ShapeTarget.Rectangulaire, false) => WallRectHosted,
                (ReservationAutoV3Window.HostTarget.Mur, ReservationAutoV3Window.ShapeTarget.Rectangulaire, true) => WallRectUnhosted,
                (ReservationAutoV3Window.HostTarget.Mur, ReservationAutoV3Window.ShapeTarget.Circulaire, false) => WallCircHosted,
                (ReservationAutoV3Window.HostTarget.Mur, ReservationAutoV3Window.ShapeTarget.Circulaire, true) => WallCircUnhosted,
                (ReservationAutoV3Window.HostTarget.Sol, ReservationAutoV3Window.ShapeTarget.Rectangulaire, false) => FloorRectHosted,
                (ReservationAutoV3Window.HostTarget.Sol, ReservationAutoV3Window.ShapeTarget.Rectangulaire, true) => FloorRectUnhosted,
                (ReservationAutoV3Window.HostTarget.Sol, ReservationAutoV3Window.ShapeTarget.Circulaire, false) => FloorCircHosted,
                (ReservationAutoV3Window.HostTarget.Sol, ReservationAutoV3Window.ShapeTarget.Circulaire, true) => FloorCircUnhosted,
                _ => null
            };
        }

        public void EnsureInitialized()
        {
            WallRectPerso ??= new ProfileConfig();
            WallCircPerso ??= new ProfileConfig();
            FloorRectPerso ??= new ProfileConfig();
            FloorCircPerso ??= new ProfileConfig();

            WallRectHosted ??= new ProfileConfig();
            WallRectUnhosted ??= new ProfileConfig();
            WallCircHosted ??= new ProfileConfig();
            WallCircUnhosted ??= new ProfileConfig();
            FloorRectHosted ??= new ProfileConfig();
            FloorRectUnhosted ??= new ProfileConfig();
            FloorCircHosted ??= new ProfileConfig();
            FloorCircUnhosted ??= new ProfileConfig();

            (WallRectHosted, WallRectUnhosted) = MigrateLegacyProfile(WallRectPerso, WallRectHosted, WallRectUnhosted);
            (WallCircHosted, WallCircUnhosted) = MigrateLegacyProfile(WallCircPerso, WallCircHosted, WallCircUnhosted);
            (FloorRectHosted, FloorRectUnhosted) = MigrateLegacyProfile(FloorRectPerso, FloorRectHosted, FloorRectUnhosted);
            (FloorCircHosted, FloorCircUnhosted) = MigrateLegacyProfile(FloorCircPerso, FloorCircHosted, FloorCircUnhosted);
        }

        private static (ProfileConfig Hosted, ProfileConfig Unhosted) MigrateLegacyProfile(
            ProfileConfig legacy,
            ProfileConfig hosted,
            ProfileConfig unhosted)
        {
            if (legacy == null || !legacy.IsConfigured || hosted.IsConfigured || unhosted.IsConfigured)
                return (hosted, unhosted);

            string name = (legacy.FamilyName + " " + legacy.TypeName).ToLowerInvariant();
            bool looksUnhosted = name.Contains("sans hôte")
                                 || name.Contains("sans hote")
                                 || name.Contains("unhost")
                                 || name.Contains("non héberg")
                                 || name.Contains("v2");

            if (looksUnhosted)
                unhosted = CopyProfile(legacy);
            else
                hosted = CopyProfile(legacy);

            return (hosted, unhosted);
        }

        private static ProfileConfig CopyProfile(ProfileConfig source)
        {
            return new ProfileConfig
            {
                FamilyName = source.FamilyName,
                TypeName = source.TypeName,
                ParamLength = source.ParamLength,
                ParamWidth = source.ParamWidth,
                ParamHeight = source.ParamHeight,
                ParamDiameter = source.ParamDiameter,
                ParamDepth = source.ParamDepth,
                VerticalPlacementMode = source.VerticalPlacementMode,
                VerticalPlacementOffsetMm = source.VerticalPlacementOffsetMm
            };
        }
    }

    public static class ReservationAutoV3PersoConfigStore
    {
        private static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence");

        public static string ConfigPath =>
            Path.Combine(Folder, "ResaPerso.json");

        public static ReservationAutoV3PersoConfig LoadOrDefault()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return new ReservationAutoV3PersoConfig();

                using (var fs = File.OpenRead(ConfigPath))
                {
                    var ser = new DataContractJsonSerializer(typeof(ReservationAutoV3PersoConfig));
                    var cfg = ser.ReadObject(fs) as ReservationAutoV3PersoConfig;
                    cfg ??= new ReservationAutoV3PersoConfig();
                    cfg.EnsureInitialized();
                    return cfg;
                }
            }
            catch
            {
                return new ReservationAutoV3PersoConfig();
            }
        }

        public static bool Save(ReservationAutoV3PersoConfig cfg, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(Folder);
                cfg ??= new ReservationAutoV3PersoConfig();
                cfg.EnsureInitialized();

                using (var fs = File.Create(ConfigPath))
                {
                    var ser = new DataContractJsonSerializer(typeof(ReservationAutoV3PersoConfig));
                    ser.WriteObject(fs, cfg);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
