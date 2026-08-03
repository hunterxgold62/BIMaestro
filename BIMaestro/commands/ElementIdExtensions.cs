//id pour version revit 2023 à 2026

using System.Reflection;

namespace Autodesk.Revit.DB
{
    internal static class ElementIdExtensions
    {
        private static readonly PropertyInfo IntegerValueProperty = typeof(ElementId).GetProperty("IntegerValue");
        private static readonly PropertyInfo ValueProperty = typeof(ElementId).GetProperty("Value");
        private static readonly ConstructorInfo LongConstructor =
            typeof(ElementId).GetConstructor(new[] { typeof(long) });
        private static readonly ConstructorInfo IntConstructor =
            typeof(ElementId).GetConstructor(new[] { typeof(int) });

        /// <summary>
        /// Crée un ElementId sans lier le binaire à ctor(Int32) ou ctor(Int64).
        /// Revit 2023 expose le constructeur Int32, tandis que Revit 2024+
        /// utilise le constructeur Int64.
        /// </summary>
        public static ElementId CreateElementId(long value)
        {
            if (LongConstructor != null)
            {
                return (ElementId)LongConstructor.Invoke(new object[] { value });
            }

            if (IntConstructor != null && value >= int.MinValue && value <= int.MaxValue)
            {
                return (ElementId)IntConstructor.Invoke(new object[] { (int)value });
            }

            throw new System.InvalidOperationException(
                "La version active de Revit ne permet pas de créer cet identifiant d'élément.");
        }

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

        public static long GetIdLongValue(this ElementId id)
        {
            if (id == null)
            {
                return -1L;
            }

            if (ValueProperty != null)
            {
                var value = ValueProperty.GetValue(id);
                if (value is long longValue)
                {
                    return longValue;
                }

                if (value is int intValue)
                {
                    return intValue;
                }
            }

            if (IntegerValueProperty != null)
            {
                var value = IntegerValueProperty.GetValue(id);
                if (value is long longValue)
                {
                    return longValue;
                }

                if (value is int intValue)
                {
                    return intValue;
                }
            }

            return -1L;
        }
    }
}
