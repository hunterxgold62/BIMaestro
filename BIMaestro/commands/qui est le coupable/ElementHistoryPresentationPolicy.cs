using System;
using System.Collections.Generic;

namespace Analyse
{
    internal static class ElementHistoryPresentationPolicy
    {
        // Revit can regenerate/delete fittings while reconnecting restored MEP
        // elements. Those implementation steps are not user deletion actions.
        // Exact names keep unrelated user/tool transactions visible.
        private static readonly HashSet<string> RestorationRepairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BIMaestro - Rétablir connexion historique",
            "BIMaestro - Réparer dimensions raccord restauré",
            "BIMaestro - Rétablir système historique indéfini",
            "BIMaestro - Rétablir relation géométrique",
            "BIMaestro - Importer definitions historiques",
            "BIMaestro - Conserver identite des nouveaux types historiques",
            "BIMaestro - Charger famille historique independante"
        };

        internal static bool IsRestorationRepair(string transaction)
            => transaction != null && RestorationRepairs.Contains(transaction.Trim());
    }
}
