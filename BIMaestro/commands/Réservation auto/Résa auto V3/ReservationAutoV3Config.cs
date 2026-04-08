using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Modification
{
    [DataContract]
    public enum VerticalPlacementReference
    {
        [EnumMember] Center = 0,
        [EnumMember] Bottom = 1,
        [EnumMember] Top = 2
    }

    [DataContract]
    public class ReservationAutoV3Config
    {
        [DataMember] public ProfileConfig WallRect { get; set; } = ProfileConfig.DefaultWallRect();
        [DataMember] public ProfileConfig WallCirc { get; set; } = ProfileConfig.DefaultWallCirc();
        [DataMember] public ProfileConfig FloorRect { get; set; } = ProfileConfig.DefaultFloorRect();
        [DataMember] public ProfileConfig FloorCirc { get; set; } = ProfileConfig.DefaultFloorCirc();

        // Oversize (mm) uniquement sur largeur/hauteur/longueur pour Pipe/Duct
        [DataMember] public double OversizeMm_PipeDuct { get; set; } = 50.0;

        // Profondeur : pas d'oversize
        [DataMember] public double OversizeMm_Depth { get; set; } = 0.0;

        [DataMember] public bool DefaultNormeEnabled { get; set; } = true;
        [DataMember] public bool DefaultDynamoAutoEnabled { get; set; } = true;

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

        // =========================
        // Correction de placement vertical
        // =========================
        // Center  : le point d’insertion de la famille est déjà au centre
        // Bottom  : le point d’insertion est en bas -> le plugin remonte de la moitié de la hauteur / profondeur
        // Top     : le point d’insertion est en haut -> le plugin redescend de la moitié de la hauteur / profondeur
        [DataMember] public VerticalPlacementReference VerticalPlacementReference { get; set; } = VerticalPlacementReference.Center;

        // Correction manuelle supplémentaire en mm
        // utile pour les familles avec logique interne de type arase inférieure / décalage incorporé
        [DataMember] public double VerticalPlacementOffsetMm { get; set; } = 0.0;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(FamilyName);

        public static ProfileConfig DefaultWallRect() => new ProfileConfig
        {
            FamilyName = "CML_Réservation rectangulaire verticale",
            TypeName = "",
            ParamLength = "Longueur",
            ParamHeight = "Hauteur",
            ParamDepth = "Profondeur",
            VerticalPlacementReference = VerticalPlacementReference.Center,
            VerticalPlacementOffsetMm = 0.0
        };

        public static ProfileConfig DefaultFloorRect() => new ProfileConfig
        {
            FamilyName = "CML_Réservation rectangulaire horizontale",
            TypeName = "",
            ParamLength = "Longueur",
            ParamWidth = "Largeur",
            ParamDepth = "Profondeur",
            VerticalPlacementReference = VerticalPlacementReference.Center,
            VerticalPlacementOffsetMm = 0.0
        };

        public static ProfileConfig DefaultWallCirc() => new ProfileConfig
        {
            FamilyName = "CML_Réservation circulaire verticale",
            TypeName = "",
            ParamDiameter = "Diamètre",
            ParamDepth = "Profondeur",
            VerticalPlacementReference = VerticalPlacementReference.Center,
            VerticalPlacementOffsetMm = 0.0
        };

        public static ProfileConfig DefaultFloorCirc() => new ProfileConfig
        {
            FamilyName = "CML_Réservation circulaire horizontale",
            TypeName = "",
            ParamDiameter = "Diamètre",
            ParamDepth = "Profondeur",
            VerticalPlacementReference = VerticalPlacementReference.Center,
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
                    return cfg ?? new ReservationAutoV3PersoConfig();
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

                using (var fs = File.Create(ConfigPath))
                {
                    var ser = new DataContractJsonSerializer(typeof(ReservationAutoV3PersoConfig));
                    ser.WriteObject(fs, cfg ?? new ReservationAutoV3PersoConfig());
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