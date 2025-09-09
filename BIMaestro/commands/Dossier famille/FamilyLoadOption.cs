using Autodesk.Revit.DB;

namespace Famille
{
    /// <summary>
    /// Ne pas écraser les valeurs des types (comportement actuel).
    /// À utiliser pour un simple "Charger" si tu veux éviter d'impacter les types existants.
    /// </summary>
    public class FamilyLoadOptionKeep : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        { overwriteParameterValues = false; return true; }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
            out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = false; return true; }
    }

    /// <summary>
    /// ÉCRASE les valeurs des types (ce qu'il faut pour "Recharger la dernière version").
    /// C'est l'équivalent de "Remplacer la version existante ET ses valeurs de paramètres".
    /// </summary>
    public class FamilyLoadOptionOverwrite : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        { overwriteParameterValues = true; return true; }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
            out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = true; return true; }
    }
}
