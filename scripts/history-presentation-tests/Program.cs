using System;
using System.IO;
using System.Text.Json;
using Analyse;

internal static class Program
{
    private static int Main(string[] args)
    {
        int checks = 0;
        void Check(bool value, string label)
        { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
        Check(ElementHistoryPresentationPolicy.IsRestorationRepair("BIMaestro - Rétablir connexion historique"), "connection regeneration is technical noise");
        Check(ElementHistoryPresentationPolicy.IsRestorationRepair("BIMaestro - Réparer dimensions raccord restauré"), "fitting repair is technical noise");
        Check(ElementHistoryPresentationPolicy.IsRestorationRepair("BIMaestro - Rétablir relation géométrique"), "geometry repair is technical noise");
        Check(ElementHistoryPresentationPolicy.IsRestorationRepair("BIMaestro - Rétablir système historique indéfini"), "system repair is technical noise");
        Check(!ElementHistoryPresentationPolicy.IsRestorationRepair("Supprime la sélection"), "real user deletion remains visible");
        Check(!ElementHistoryPresentationPolicy.IsRestorationRepair("BIMaestro - Restaurer élément supprimé"), "primary restoration remains visible");
        Check(!ElementHistoryPresentationPolicy.IsRestorationRepair("BIMaestro - Restaurer éléments supprimés"), "bulk restoration remains visible");
        Check(!ElementHistoryPresentationPolicy.IsRestorationRepair("Rétablir connexion historique"), "similar user transaction remains visible");
        Check(!ElementHistoryPresentationPolicy.IsRestorationRepair("BIMaestro - Rétablir connexion historique utilisateur"), "prefix match cannot hide other operations");
        Check(!ElementHistoryPresentationPolicy.IsRestorationRepair(null), "unknown transaction remains visible");
        if (args.Length > 0)
        {
            int repairs = 0, userDeletes = 0;
            foreach (var line in File.ReadLines(args[0]))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var tx = root.TryGetProperty("Tx", out var transaction) ? transaction.GetString() : null;
                var action = root.TryGetProperty("Action", out var actionValue) ? actionValue.GetString() : null;
                if (ElementHistoryPresentationPolicy.IsRestorationRepair(tx)) repairs++;
                else if (action == "delete" && tx == "Supprime la sélection") userDeletes++;
            }
            Check(repairs > 0, "replay finds and hides existing repair events");
            Check(userDeletes >= 1291, "replay preserves the complete 1291-element user deletion");
            Console.WriteLine($"Read-only journal replay: {repairs} repair events hidden; {userDeletes} genuine user deletion events preserved.");
        }
        Console.WriteLine(checks + " assertions passed.");
        return 0;
    }
}
