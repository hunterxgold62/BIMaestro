using Autodesk.Revit.DB;

namespace Famille
{
    public class FamilyLoadOption : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = false;
            return true;  // écrase sans demander
        }

        public bool OnSharedFamilyFound(Family sharedFamily,
                                        bool familyInUse,
                                        out FamilySource source,
                                        out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = false;
            return true;
        }
    }
}
