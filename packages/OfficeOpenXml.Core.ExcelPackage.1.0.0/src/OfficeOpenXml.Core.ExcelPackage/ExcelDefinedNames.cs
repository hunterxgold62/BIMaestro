using System;
using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;

namespace OfficeOpenXml.Core.ExcelPackage
{
    public class ExcelDefinedNames
    {
        ExcelWorksheets _worksheets;
        XDocument _worsheetsXml;
        XElement _definedNames;

        protected internal ExcelDefinedNames(ExcelWorksheets worksheets, XDocument worksheetsXml)
        {
            _worksheets = worksheets;
            _worsheetsXml = worksheetsXml;

            _definedNames = worksheetsXml.XPathSelectElement("//d:definedNames", _worksheets.NsManager);
        }

        public void Add(String name, String rangeRef)
        {
            if (!IsValidName(name)) { throw new ArgumentException("name"); }
            if (!IsValidRangeRef(rangeRef)) { throw new ArgumentException("Invalid rangeRef"); }

            if (Contains(name)) { throw new ArgumentException("Already exists: " + name); }

            // Create list element if needed
            if (_definedNames == null)
            {
                var wbNode = _worsheetsXml.XPathSelectElement("//d:workbook", _worksheets.NsManager);
                if (wbNode == null) { throw new NullReferenceException("Workbook node missing."); }

                _definedNames = ExtensonMethods.NewElement("definedNames");
                wbNode.Add(_definedNames);
            }

            var dnElement = ExtensonMethods.NewElement("definedName").SetAttribute("name", name);
            dnElement.Value = rangeRef;
            _definedNames.Add(dnElement);
        }

        bool IsValidRangeRef(String rangeRef)
        {
            return !String.IsNullOrEmpty(rangeRef) &&
                    rangeRef.Contains("!") &&
                    rangeRef.Contains(":");
        }
        bool IsValidName(String name)
        {
            return !String.IsNullOrEmpty(name) &&
                !name.Contains(" ") &&
                char.IsLetter(name[0]);
        }

        public void Remove(String name)
        {
            if (_definedNames == null) { return; }
            
            var dnNode = GetSingleNameNode(name);
            if (dnNode == null) { return; }
	        dnNode.Remove();
        }

        public string this[String name]
        {
            get 
            {
                var dnNode = GetSingleNameNode(name);
                if (dnNode == null) { return null; }
                return dnNode.Value; 
            }
            set 
            {
                if (value == null)
                {
                    Remove(name);
                    return;
                }

                var dnNode = GetSingleNameNode(name);
                if (dnNode == null)
                {
                    Add(name, value);
                    return;
                }

                if (!IsValidRangeRef(value)) { throw new ArgumentException("Invalid rangeRef"); }
                dnNode.Value = value;
            }
        }

        public bool Contains(String name)
        {
            return GetSingleNameNode(name) != null;
        }

        XElement GetSingleNameNode(String name)
        {
            if (_definedNames == null) { return null; }
            foreach (var dnNode in _definedNames.Nodes().Cast<XElement>())
            {
                if (dnNode.Attribute("name").Value == name) { return dnNode; }
            }
            return null;
        }

        /// <summary>
        /// Get a range reference in Excel format
        /// e.g. GetRange("sheet", 1,5,10,2) => "sheet!$J$1:$K$5"
        /// </summary>
        /// <param name="worksheet"></param>
        /// <param name="startRow"></param>
        /// <param name="rowCount">Number of rows to include, >=1</param>
        /// <param name="startCol"></param>
        /// <param name="colCount">Number of columns to include, >=1</param>
        /// <returns></returns>
        public static String GetRangeRef(String worksheet,
            int startRow, int rowCount, int startCol, int colCount)
        {
            // Be tolerant
            if (rowCount <= 0) { rowCount = 1; }
            if (colCount <= 0) { colCount = 1; }

            return worksheet + "!" +
                "$" + ExcelCell.GetColumnLetter(startCol) + "$" + startRow + ":" +
                "$" + ExcelCell.GetColumnLetter(startCol + colCount -1) + "$" + (startRow + rowCount - 1);
        }
    }
}
