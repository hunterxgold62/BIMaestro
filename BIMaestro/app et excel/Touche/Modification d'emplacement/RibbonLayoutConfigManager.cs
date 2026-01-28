using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace BIMaestro.RibbonLayout
{
    public class RibbonLayoutConfig
    {
        public List<RibbonPanelConfig> Panels { get; set; } = new List<RibbonPanelConfig>();
    }

    public class RibbonPanelConfig
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Buttons { get; set; } = new List<string>();
    }

    public static class RibbonLayoutConfigManager
    {
        private const string ConfigFileName = "RibbonLayout.json";

        public static RibbonLayoutConfig LoadLayout(IEnumerable<RibbonPanelDefinition> definitions)
        {
            var config = LoadFromFile();
            var normalized = Normalize(config, definitions);
            return normalized ?? BuildDefault(definitions);
        }

        public static void SaveLayout(RibbonLayoutConfig config)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GetConfigPath()) ?? string.Empty);
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(GetConfigPath(), json);
            }
            catch
            {
                // Ignorer les erreurs d'écriture pour ne pas bloquer l'utilisateur
            }
        }

        public static string GetConfigPath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, ConfigFileName);
        }

        public static RibbonLayoutConfig BuildDefault(IEnumerable<RibbonPanelDefinition> definitions)
        {
            return new RibbonLayoutConfig
            {
                Panels = definitions
                    .Select(panel => new RibbonPanelConfig
                    {
                        Name = panel.Name,
                        Buttons = panel.Items.Select(i => i.Id).ToList()
                    })
                    .ToList()
            };
        }

        private static RibbonLayoutConfig? LoadFromFile()
        {
            var path = GetConfigPath();
            if (!File.Exists(path)) return null;

            try
            {
                var content = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<RibbonLayoutConfig>(content);
            }
            catch
            {
                return null;
            }
        }

        private static RibbonLayoutConfig? Normalize(RibbonLayoutConfig? config, IEnumerable<RibbonPanelDefinition> definitions)
        {
            if (config == null) return null;

            var panelLookup = definitions.ToDictionary(d => d.Name, d => d);
            var normalizedPanels = new List<RibbonPanelConfig>();

            foreach (var panel in config.Panels)
            {
                if (!panelLookup.TryGetValue(panel.Name, out var def)) continue;

                var buttons = panel.Buttons
                    .Where(id => def.Items.Any(i => i.Id == id))
                    .Distinct()
                    .ToList();

                foreach (var missing in def.Items.Select(i => i.Id).Where(id => !buttons.Contains(id)))
                {
                    buttons.Add(missing);
                }

                normalizedPanels.Add(new RibbonPanelConfig
                {
                    Name = def.Name,
                    Buttons = buttons
                });
            }

            foreach (var def in definitions)
            {
                if (normalizedPanels.Any(p => p.Name == def.Name)) continue;
                normalizedPanels.Add(new RibbonPanelConfig
                {
                    Name = def.Name,
                    Buttons = def.Items.Select(i => i.Id).ToList()
                });
            }

            return new RibbonLayoutConfig { Panels = normalizedPanels };
        }
    }
}