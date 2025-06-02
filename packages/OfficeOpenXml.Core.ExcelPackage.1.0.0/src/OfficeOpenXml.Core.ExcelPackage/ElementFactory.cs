using System.Xml.Linq;

namespace OfficeOpenXml.Core.ExcelPackage
{
    public class ElementFactory
    {
        public string Namespace { get; set; }

        public ElementFactory(string ns)
        {
            Namespace = ns;
        }

        public XElement New(string name, object content)
        {
            return ExtensonMethods.NewElement(name, Namespace, content);
        }

        public XElement New(string name, params object[] content)
        {
            return ExtensonMethods.NewElement(name, Namespace, content);
        }
    }
}