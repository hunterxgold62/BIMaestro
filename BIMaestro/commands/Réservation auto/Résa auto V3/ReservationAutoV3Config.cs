using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Modification
{
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

        public bool IsConfigured => !string.IsNullOrWhiteSpace(FamilyName);

        public static ProfileConfig DefaultWallRect() => new ProfileConfig
        {
            FamilyName = "CML_Réservation rectangulaire verticale",
            TypeName = "",
            ParamLength = "Longueur",
            ParamHeight = "Hauteur",
            ParamDepth = "Profondeur"
        };

        public static ProfileConfig DefaultFloorRect() => new ProfileConfig
        {
            FamilyName = "CML_Réservation rectangulaire horizontale",
            TypeName = "",
            ParamLength = "Longueur",
            ParamWidth = "Largeur",
            ParamDepth = "Profondeur"
        };

        public static ProfileConfig DefaultWallCirc() => new ProfileConfig
        {
            FamilyName = "CML_Réservation circulaire verticale",
            TypeName = "",
            ParamDiameter = "Diamètre",
            ParamDepth = "Profondeur"
        };

        public static ProfileConfig DefaultFloorCirc() => new ProfileConfig
        {
            FamilyName = "CML_Réservation circulaire horizontale",
            TypeName = "",
            ParamDiameter = "Diamètre",
            ParamDepth = "Profondeur"
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
}
