/* 
 * You may amend and distribute as you like, but don't remove this header!
 * 
 * ExcelPackage provides server-side generation of Excel 2007 spreadsheets.
 * See http://www.codeplex.com/ExcelPackage for details.
 * 
 * Copyright 2007 © Dr John Tunnicliffe 
 * mailto:dr.john.tunnicliffe@btinternet.com
 * All rights reserved.
 * 
 * ExcelPackage is an Open Source project provided under the 
 * GNU General Public License (GPL) as published by the 
 * Free Software Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
 * 
 * The GNU General Public License can be viewed at http://www.opensource.org/licenses/gpl-license.php
 * If you unfamiliar with this license or have questions about it, here is an http://www.gnu.org/licenses/gpl-faq.html
 * 
 * The code for this project may be used and redistributed by any means PROVIDING it is 
 * not sold for profit without the author's written consent, and providing that this notice 
 * and the author's name and all copyright notices remain intact.
 * 
 * All code and executables are provided "as is" with no warranty either express or implied. 
 * The author accepts no liability for any damage or loss of business that this product may cause.
 */

/*
 * Code change notes:
 * 
 * Author							Change						Date
 * ******************************************************************************
 * John Tunnicliffe		Initial Release		01-Jan-2007
 * ******************************************************************************
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace OfficeOpenXml.Core.ExcelPackage
{
	/// <summary>
	/// Provides enumeration through all the worksheets in the workbook
	/// </summary>
	public class ExcelWorksheets : IEnumerable
	{
		#region ExcelWorksheets Private Properties
		private List<ExcelWorksheet> _worksheets;
		private ExcelPackage _xlPackage;
		private XmlNamespaceManager _nsManager;
		private XElement _worksheetsNode;

        ExcelDefinedNames _definedNames;
		#endregion

		#region ExcelWorksheets Constructor
		/// <summary>
		/// Creates a new instance of the ExcelWorksheets class.
		/// For internal use only!
		/// </summary>
		/// <param name="xlPackage"></param>
		protected internal ExcelWorksheets(ExcelPackage xlPackage)
		{
			_xlPackage = xlPackage;
			//  Create a NamespaceManager to handle the default namespace, 
			//  and create a prefix for the default namespace:
			NameTable nt = new NameTable();
			_nsManager = new XmlNamespaceManager(nt);
			_nsManager.AddNamespace("d", ExcelPackage.schemaMain.NamespaceName);
			_nsManager.AddNamespace("r", ExcelPackage.schemaRelationships.NamespaceName);

			// obtain container node for all worksheets
			_worksheetsNode = _xlPackage.Workbook.WorkbookXml.XPathSelectElement("//d:sheets", _nsManager);
			if (_worksheetsNode == null)
			{
				// create new node as it did not exist
				_worksheetsNode = ExtensonMethods.NewElement("sheets", ExcelPackage.schemaMain);
				_xlPackage.Workbook.WorkbookXml.Document.Add(_worksheetsNode);
			}

			_worksheets = new List<ExcelWorksheet>();
			foreach (var sheetNode in _worksheetsNode.Nodes().Cast<XElement>())
			{
				string name = sheetNode.Attribute("name").Value;
				//  Get the relationship id attribute:
				string relId = sheetNode.Attribute("r:id").Value;
				int sheetID = Convert.ToInt32(sheetNode.Attribute("sheetId").Value);
				//if (sheetID != count)
				//{
				//  // renumber the sheets as they are in an odd order
				//  sheetID = count;
				//  sheetNode.Attributes["sheetId"].Value = sheetID.ToString();
				//}
				// get hidden attribute (if present)
				bool hidden = false;
				var attr = sheetNode.Attribute("hidden");
				if (attr != null)
					hidden = Convert.ToBoolean(attr.Value);

				//string type = "";
				//attr = sheetNode.Attributes["type"];
				//if (attr != null)
				//  type = attr.Value;

				PackageRelationship sheetRelation = _xlPackage.Workbook.Part.GetRelationship(relId);
				Uri uriWorksheet = PackUriHelper.ResolvePartUri(_xlPackage.Workbook.WorkbookUri, sheetRelation.TargetUri);
				
				// add worksheet to our collection
				_worksheets.Add(new ExcelWorksheet(_xlPackage, relId, name, uriWorksheet, sheetID, hidden));
			}

            _definedNames = new ExcelDefinedNames(this, _xlPackage.Workbook.WorkbookXml);
		}
		#endregion

		#region ExcelWorksheets Public Properties
		/// <summary>
		/// Returns the number of worksheets in the workbook
		/// </summary>
		public int Count
		{
			get { return (_worksheets.Count); }
		}

        public ExcelDefinedNames DefinedNames
        {
            get { return _definedNames; }
        }

		#endregion

		#region ExcelWorksheets Public Methods
		/// <summary>
		/// Returns an enumerator that allows the foreach syntax to be used to 
		/// itterate through all the worksheets
		/// </summary>
		/// <returns>An enumerator</returns>
		public IEnumerator GetEnumerator()
		{
			return (_worksheets.GetEnumerator());
		}

		#region Add Worksheet
		/// <summary>
		/// Adds a blank worksheet with the desired name
		/// </summary>
		/// <param name="Name"></param>
        public ExcelWorksheet Add(string Name)
        {
            return Add(Name, 0, 0);
        }

        /// <summary>
        /// Adds a blank worksheet with the desired name
        /// </summary>
        /// <param name="Name"></param>
        /// <param name="rowCount">Number of rows to to create initially (for performance)</param>
        /// <param name="colCount">Number of columns to to create</param>
        /// <returns></returns>
        public ExcelWorksheet Add(string Name, int rowCount, int colCount)
		{
			// first find maximum existing sheetID
			// also check the name is unique - if not throw an error
			int sheetID = 0;
			foreach (var sheet in _worksheetsNode.Nodes().Cast<XElement>())
			{
				var attr = sheet.Attribute("sheetId");
				if (attr != null)
				{
					int curID = int.Parse(attr.Value);
					if (curID > sheetID)
						sheetID = curID;
				}
				attr = sheet.Attribute("name");
				if (attr != null)
				{
					if (attr.Value == Name)
						throw new Exception("Add worksheet Error: attempting to create worksheet with duplicate name");
				}
			}
			// we now have the max existing values, so add one
			sheetID++;

			// add the new worksheet to the package
			Uri uriWorksheet = new Uri("/xl/worksheets/sheet" + sheetID.ToString() + ".xml", UriKind.Relative);
            PackagePart worksheetPart = _xlPackage.Package.CreatePart(uriWorksheet, @"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml", CompressionOption.Normal);

			// create the new, empty worksheet and save it to the package
			StreamWriter streamWorksheet = new StreamWriter(worksheetPart.GetStream(FileMode.Create, FileAccess.Write));
			var worksheetXml = CreateNewWorksheet();
			worksheetXml.Save(streamWorksheet);
			streamWorksheet.Dispose();
			_xlPackage.Package.Flush();
			
			// create the relationship between the workbook and the new worksheet
			PackageRelationship rel = _xlPackage.Workbook.Part.CreateRelationship(uriWorksheet, TargetMode.Internal, ExcelPackage.schemaRelationships.NamespaceName + "/worksheet");
			_xlPackage.Package.Flush();
			
			// now create the new worksheet tag and set name/SheetId attributes in the workbook.xml
	        var worksheetNode = ExtensonMethods.NewElement("sheet");
			// create the new sheet node
			worksheetNode.SetAttribute("name", Name);
			worksheetNode.SetAttribute("sheetId", sheetID.ToString());
			// set the r:id attribute
			worksheetNode.SetAttribute("id", ExcelPackage.schemaRelationships, rel.Id);
			// insert the sheet tag with all attributes set as above
			_worksheetsNode.Add(worksheetNode);

			// create a reference to the new worksheet in our collection
			ExcelWorksheet worksheet = new ExcelWorksheet(_xlPackage, rel.Id, Name, uriWorksheet, sheetID, false);
			_worksheets.Add(worksheet);
            worksheet.CreateEmptyCells(rowCount, colCount);

			return worksheet;
		}

		/// <summary>
		/// Creates the XML document representing a new empty worksheet
		/// </summary>
		/// <returns></returns>
		protected internal XDocument CreateNewWorksheet()
		{			
			// create the new worksheet
			var worksheetXml = new XDocument(
				ExtensonMethods.NewElement("worksheet",
					ExtensonMethods.NewElement("sheetViews",
						ExtensonMethods.NewElement("sheetView").SetAttribute("workbookViewId", "0"))).SetAttribute("xmlns:r", ExcelPackage.schemaRelationships.NamespaceName),
				// create the empty sheetData tag (must be present, but can be empty)
				ExtensonMethods.NewElement("sheetData"));
			return worksheetXml;
		}
		#endregion

		#region Delete Worksheet
		/// <summary>
		/// Delete a worksheet from the workbook package
		/// </summary>
		/// <param name="positionID">The position of the worksheet in the workbook</param>
		public void Delete(int positionID)
		{
			if (_worksheets.Count == 1)
				throw new Exception("Error: You are attempting to delete the last worksheet in the workbook.  One worksheet MUST be present in the workbook!");
            if (positionID < 1)
                throw new ArgumentException("Index should be 1-based", "positionID");
            
            ExcelWorksheet worksheet = _worksheets[positionID - 1];

			// delete the worksheet from the package 
			_xlPackage.Package.DeletePart(worksheet.WorksheetUri);

			// delete the relationship from the package 
			_xlPackage.Workbook.Part.DeleteRelationship(worksheet.RelationshipID);

			// delete worksheet from the workbook XML
			var sheetsNode = _xlPackage.Workbook.WorkbookXml.XPathSelectElement("//d:workbook/d:sheets", _nsManager);
			if (sheetsNode != null)
			{
				var sheetNode = sheetsNode.XPathSelectElement($"./d:sheet[@sheetId={worksheet.SheetID}]", _nsManager);
				sheetNode?.Remove();
			}

            // delete worksheet from the list
			_worksheets.RemoveAt(positionID - 1);
		}
		#endregion

		/// <summary>
		/// Returns the worksheet at the specified position.  
		/// </summary>
		/// <param name="positionID">The position of the worksheet. 1-base</param>
		/// <returns></returns>
		public ExcelWorksheet this[int positionID]
		{
			get
			{
                if (positionID < 1)
                    throw new ArgumentException("Index should be 1-based", "positionID");

				return (_worksheets[positionID-1]);
			}
		}

		/// <summary>
		/// Returns the worksheet matching the specified name
		/// </summary>
        /// <remarks>If multiple sheets have the same name, returns the first one</remarks>
		/// <param name="Name">The name of the worksheet</param>
		/// <returns></returns>
		public ExcelWorksheet this[string Name]
		{
			get
			{
				foreach (ExcelWorksheet worksheet in _worksheets)
				{
                    if (worksheet.Name == Name) { return worksheet; }
				}
                return null;
			}
		}

		/// <summary>
		/// Copies the named worksheet and creates a new worksheet in the same workbook
		/// </summary>
		/// <param name="Name">The name of the existing worksheet</param>
		/// <param name="NewName">The name of the new worksheet to create</param>
		/// <returns></returns>
		public ExcelWorksheet Copy(string Name, string NewName)
		{
			// TODO: implement copy worksheet
			throw new Exception("The method or operation is not implemented.");
		}
		#endregion

        internal XmlNamespaceManager NsManager
        {
            get { return _nsManager; }
        }
	} // end class Worksheets
}
