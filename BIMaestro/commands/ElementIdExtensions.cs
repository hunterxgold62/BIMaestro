//id pour version revit 2026

using System.Reflection;

namespace Autodesk.Revit.DB
{
    internal static class ElementIdExtensions
    {
        private static readonly PropertyInfo IntegerValueProperty = typeof(ElementId).GetProperty("IntegerValue");
        private static readonly PropertyInfo ValueProperty = typeof(ElementId).GetProperty("Value");

        public static int GetIdValue(this ElementId id)
        {
            if (id == null)
            {
                return -1;
            }

            if (ValueProperty != null)
            {
                var value = ValueProperty.GetValue(id);
                if (value is long longValue)
                {
                    return unchecked((int)longValue);
                }

                if (value is int intValue)
                {
                    return intValue;
                }
            }

            if (IntegerValueProperty != null)
            {
                var value = IntegerValueProperty.GetValue(id);
                if (value is int intValue)
                {
                    return intValue;
                }

                if (value is long longValue)
                {
                    return unchecked((int)longValue);
                }
            }

            return -1;
        }
    }
}