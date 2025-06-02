using System.Xml.Linq;

namespace OfficeOpenXml.Core.ExcelPackage
{
    internal static class ExtensonMethods
	{
		public static XElement NewElement(string name, string @namespace, object content)
		{
			return new XElement(name, content).AddSchemaAttribute(@namespace);
		}

		public static XElement NewElement(string name, string @namespace, params object[] content)
		{
			return new XElement(name, content).AddSchemaAttribute(@namespace);
		}

		public static XElement NewElement(string name, object content)
		{
			return NewElement(name, ExcelPackage.schemaMain, content);
		}

		public static XElement NewElement(string name, params object[] content)
		{
			return NewElement(name, ExcelPackage.schemaMain, content);
		}
	}
}