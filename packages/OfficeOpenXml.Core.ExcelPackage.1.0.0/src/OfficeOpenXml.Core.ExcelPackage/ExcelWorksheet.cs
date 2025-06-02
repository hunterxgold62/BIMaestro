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
	/// Represents an Excel worksheet and provides access to its properties and methods
	/// </summary>
	public class ExcelWorksheet
	{
		#region Worksheet Private Properties
		/// <summary>
		/// Temporary tag for all column numbers in the worksheet XML
		/// For internal use only!
		/// </summary>
		protected internal const string tempColumnNumberTag = "colNumber";
		/// <summary>
		/// Reference to the parent package
		/// For internal use only!
		/// </summary>
		protected internal ExcelPackage xlPackage;
		private Uri _worksheetUri;
		private string _name;
		private int _sheetID;
		private bool _hidden;
		private string _relationshipID;
		private XDocument _worksheetXml;
		private ExcelWorksheetView _sheetView;
		private ExcelHeaderFooter _headerFooter;
		private XmlNamespaceManager _nsManager;
		#endregion  // END Worksheet Private Properties

		#region ExcelWorksheet Constructor
		/// <summary>
		/// Creates a new instance of ExcelWorksheet class. 
		/// For internal use only!
		/// </summary>
		/// <param name="ParentXlPackage">Parent ExcelPackage object</param>
		/// <param name="RelationshipID">Package relationship ID</param>
		/// <param name="sheetName">Name of the new worksheet</param>
		/// <param name="uriWorksheet">Uri of the worksheet in the package</param>
		/// <param name="SheetID">The worksheet's ID in the workbook XML</param>
		/// <param name="Hide">Indicates if the worksheet is hidden</param>
		protected internal ExcelWorksheet(
			ExcelPackage ParentXlPackage,
			string RelationshipID,
			string sheetName,
			Uri uriWorksheet,
			int SheetID,
			bool Hide)
		{
			xlPackage = ParentXlPackage;
			_relationshipID = RelationshipID;
			_worksheetUri = uriWorksheet;
			_name = sheetName;
			//_type = Type;
			_sheetID = SheetID;
			Hidden = Hide;
		}
		#endregion

		#region Worksheet Public Properties
		/// <summary>
		/// Read-only: the Uri to the worksheet within the package
		/// </summary>
		protected internal Uri WorksheetUri { get { return (_worksheetUri); } }
		/// <summary>
		/// Read-only: a reference to the PackagePart for the worksheet within the package
		/// </summary>
		protected internal PackagePart Part { get { return (xlPackage.Package.GetPart(WorksheetUri)); } }
		/// <summary>
		/// Read-only: the ID for the worksheet's relationship with the workbook in the package
		/// </summary>
		protected internal string RelationshipID { get { return (_relationshipID); } }
		/// <summary>
		/// The unique identifier for the worksheet.  Note that these can be random, so not
		/// too useful in code!
		/// </summary>
		protected internal int SheetID { get { return (_sheetID); } }
		/// <summary>
		/// Provides access to a namespace manager instance to allow XPath searching
		/// </summary>
		public XmlNamespaceManager NameSpaceManager
		{
			get
			{
				if (_nsManager == null)
				{
					NameTable nt = new NameTable();
					_nsManager = new XmlNamespaceManager(nt);
					_nsManager.AddNamespace("d", ExcelPackage.schemaMain.NamespaceName);
				}
				return (_nsManager);
			}
		}
		/// <summary>
		/// Returns a ExcelWorksheetView object that allows you to
		/// set the view state properties of the worksheet
		/// </summary>
		public ExcelWorksheetView View
		{
			get
			{
				if (_sheetView == null)
				{
					_sheetView = new ExcelWorksheetView(this);
				}
				return (_sheetView);
			}
		}

		#region Name // Worksheet Name
		/// <summary>
		/// The worksheet's name as it appears on the tab
		/// </summary>
		public string Name
		{
			get { return (_name); }
			set
			{
				var sheetNode = xlPackage.Workbook.WorkbookXml.XPathSelectElement(string.Format("//d:sheet[@sheetId={0}]", _sheetID), NameSpaceManager);
				var nameAttr = sheetNode?.Attribute("name");
				if (nameAttr != null)
				{
					nameAttr.Value = value;
				}
				_name = value;
			}
		}
		#endregion // END Worksheet Name

		#region Hidden
		/// <summary>
		/// Indicates if the worksheet is hidden in the workbook
		/// </summary>
		public bool Hidden
		{
			get { return (_hidden); }
			set
			{
				var sheetNode = xlPackage.Workbook.WorkbookXml.XPathSelectElement(string.Format("//d:sheet[@sheetId={0}]", _sheetID), NameSpaceManager);
				var nameAttr = sheetNode?.Attribute("hidden");
				if (nameAttr != null)
				{
					nameAttr.Value = value.ToString();
				}
				_hidden = value;
			}
		}
		#endregion

		#region defaultRowHeight
		/// <summary>
		/// Allows you to get/set the default height of all rows in the worksheet
		/// </summary>
		public int defaultRowHeight
		{
			get
			{
				int retValue = 15; // Excel's default height
				var sheetFormat = WorksheetXml.XPathSelectElement("//d:sheetFormatPr", NameSpaceManager);
				if (sheetFormat != null)
				{
					string ret = sheetFormat.Attribute("defaultRowHeight").Value;
					if (ret != "")
						retValue = int.Parse(ret);
				}
				return retValue;
			}
			set
			{
				var sheetFormat = WorksheetXml.XPathSelectElement("//d:sheetFormatPr", NameSpaceManager);
				if (sheetFormat == null)
				{
					// create the node as it does not exist
					sheetFormat = ExtensonMethods.NewElement("sheetFormatPr");
					// find location to insert new element
					var sheetViews = WorksheetXml.XPathSelectElement("//d:sheetViews", NameSpaceManager);
					// insert the new node
					sheetFormat.AddAfterSelf(sheetViews);
				}
				sheetFormat.SetAttribute("defaultRowHeight", value.ToString());
			}
		}
		#endregion

		#region WorksheetXml
		/// <summary>
		/// The XML document holding all the worksheet data.
		/// </summary>
		public XDocument WorksheetXml
		{
			get
			{
				if (_worksheetXml == null)
				{
					PackagePart packPart = xlPackage.Package.GetPart(WorksheetUri);
					_worksheetXml = XDocument.Load(packPart.GetStream());
					// convert worksheet into the type of XML we like dealing with
					AddNumericCellIDs();
				}
				return (_worksheetXml);
			}
		}
		#endregion

		#region HeaderFooter
		/// <summary>
		/// A reference to the header and footer class which allows you to 
		/// set the header and footer for all odd, even and first pages of the worksheet
		/// </summary>
		public ExcelHeaderFooter HeaderFooter
		{
			get
			{
				if (_headerFooter == null)
				{
					var headerFooterNode = WorksheetXml.XPathSelectElement("//d:headerFooter", NameSpaceManager);
					if (headerFooterNode == null)
					{
						headerFooterNode = ExtensonMethods.NewElement("headerFooter");
						WorksheetXml.Document.Add(headerFooterNode);
					}
					_headerFooter = new ExcelHeaderFooter(headerFooterNode);
				}
				return (_headerFooter);
			}
		}
		#endregion

		// TODO: implement freeze pane. 
		// TODO: implement page margin properties

		#endregion // END Worksheet Public Properties

		#region Worksheet Public Methods

		/// <summary>
		/// Provides access to an individual row within the worksheet so you can set its properties.
		/// </summary>
		/// <param name="rowNum">The row number in the worksheet</param>
		/// <returns></returns>
		public ExcelRow Row(int rowNum)
		{
			ExcelRow row;
			if (Rows.TryGetValue(rowNum, out row)) { return row; }

			row = new ExcelRow(this, rowNum);
			Rows.Add(rowNum, row);
			return row;
		}

		/// <summary>
		/// Provides access to an individual cell within the worksheet.
		/// </summary>
		/// <param name="rowNum">The row number in the worksheet</param>
		/// <param name="colNum">The column number in the worksheet</param>
		/// <returns></returns>
		public ExcelCell Cell(int rowNum, int colNum)
		{
			ExcelRow row = Row(rowNum);
			ExcelCell cell;
			if (row.Cells.TryGetValue(colNum, out cell)) { return cell; }

			cell = new ExcelCell(this, rowNum, colNum);
			row.Cells.Add(colNum, cell);
			return cell;
		}

		Dictionary<int, ExcelRow> _rows;
		Dictionary<int, ExcelRow> Rows
		{
			get
			{
				return _rows ?? (_rows = ReadData());
			}
		}
		Dictionary<int, ExcelRow> ReadData()
		{
			Dictionary<int, ExcelRow> rows = new Dictionary<int, ExcelRow>();
			foreach (var rowElement in WorksheetXml.XPathSelectElements("//d:sheetData/d:row", NameSpaceManager))
			{
				int rowNum = Convert.ToInt32(rowElement.Attribute("r").Value);
				ExcelRow row = new ExcelRow(this, rowElement);
				rows.Add(rowNum, row);

				// Get all cells for the row
				foreach (var cellElement in rowElement.XPathSelectElements("./d:c", NameSpaceManager))
				{
					int colNum = Convert.ToInt32(cellElement.AttributeValue(ExcelWorksheet.tempColumnNumberTag));
					ExcelCell cell = new ExcelCell(this, cellElement, rowNum, colNum);
					row.Cells.Add(colNum, cell);
				}
			}
			return rows;
		}

		void ShiftRows(int start, int change)
		{
			Dictionary<int, ExcelRow> newRows = new Dictionary<int, ExcelRow>();
			foreach (int rowNum in Rows.Keys)
			{
				ExcelRow row = Rows[rowNum];
				int newRowNum = (rowNum >= start) ? rowNum + 1 : rowNum;
				newRows.Add(newRowNum, row);
			}
			_rows = newRows;
		}

		/// <summary>
		/// Create empty rows and cols to improve performance.
		/// </summary>
		/// <param name="rowCount"></param>
		/// <param name="colCount"></param>
		internal void CreateEmptyCells(int rowCount, int colCount)
		{
			if (Rows.Count != 0) { throw new InvalidOperationException("Must be called before rows are filled"); }

			var sheetDataNode = WorksheetXml.XPathSelectElement("//d:sheetData", NameSpaceManager);
			for (int rowNum = 1; rowNum <= rowCount; rowNum++)
			{
				// Add element
				var rowElement = ExtensonMethods.NewElement("row");
				rowElement.SetAttribute("r", rowNum.ToString());
				sheetDataNode.Add(rowElement);

				ExcelRow row = new ExcelRow(this, rowElement);
				Rows.Add(rowNum, row);

				for (int colNum = 1; colNum <= colCount; colNum++)
				{
					var cellElement = ExtensonMethods.NewElement("c");
					cellElement.SetAttribute(ExcelWorksheet.tempColumnNumberTag, colNum.ToString());
					rowElement.Add(cellElement);

					ExcelCell cell = new ExcelCell(this, cellElement, rowNum, colNum);
					row.Cells.Add(colNum, cell);
				}
			}
		}

		/// <summary>
		/// Provides access to an individual column within the worksheet so you can set its properties.
		/// </summary>
		/// <param name="col">The column number in the worksheet</param>
		/// <returns></returns>
		public ExcelColumn Column(int col)
		{
			return (new ExcelColumn(this, col));
		}

		#region CreateSharedFormula
		/// <summary>
		/// Creates a shared formula based on the formula already in startCell
		/// Essentially this supports the formula attributes such as t="shared" ref="B2:B4" si="0"
		/// as per Brian Jones: Open XML Formats blog. See
		/// http://blogs.msdn.com/brian_jones/archive/2006/11/15/simple-spreadsheetml-file-part-2-of-3.aspx
		/// </summary>
		/// <param name="startCell">The cell containing the formula</param>
		/// <param name="endCell">The end cell (i.e. end of the range)</param>
		public void CreateSharedFormula(ExcelCell startCell, ExcelCell endCell)
		{
			XElement formulaElement;
			string formula = startCell.Formula;
			if (formula == "") throw new Exception("CreateSharedFormula Error: startCell does not contain a formula!");

			// find or create a shared formula ID
			int sharedID = -1;
			foreach (var node in _worksheetXml.XPathSelectElements("//d:sheetData/d:row/d:c/d:f/@si", NameSpaceManager))
			{
				int curID = int.Parse(node.Value);
				if (curID > sharedID) sharedID = curID;
			}
			sharedID++;  // first value must be zero

			for (int row = startCell.Row; row <= endCell.Row; row++)
			{
				for (int col = startCell.Column; col <= endCell.Column; col++)
				{
					ExcelCell cell = Cell(row, col);

					// to force Excel to re-calculate the formula, we must remove the value
					cell.RemoveValue();

					formulaElement = (XElement)cell.Element.XPathSelectElement("./d:f", NameSpaceManager);
					if (formulaElement == null)
					{
						formulaElement = cell.AddFormulaElement();
					}
					formulaElement.SetAttribute("t", "shared");
					formulaElement.SetAttribute("si", sharedID.ToString());
				}
			}

			// finally add the shared cell range to the startCell
			formulaElement = (XElement)startCell.Element.XPathSelectElement("./d:f", NameSpaceManager);
			formulaElement.SetAttribute("ref", string.Format("{0}:{1}", startCell.CellAddress, endCell.CellAddress));
		}
		#endregion

		/// <summary>
		/// Inserts conditional formatting for the cell range.
		/// Currently only supports the dataBar style.
		/// </summary>
		/// <param name="startCell"></param>
		/// <param name="endCell"></param>
		/// <param name="color"></param>
		public void CreateConditionalFormatting(ExcelCell startCell, ExcelCell endCell, string color)
		{
			var formatNode = WorksheetXml.XPathSelectElement("//d:conditionalFormatting", NameSpaceManager);
			if (formatNode == null)
			{
				formatNode = ExtensonMethods.NewElement("conditionalFormatting");
				var prevNode = WorksheetXml.XPathSelectElement("//d:mergeCells", NameSpaceManager);
				if (prevNode == null)
					prevNode = WorksheetXml.XPathSelectElement("//d:sheetData", NameSpaceManager);
				prevNode.AddAfterSelf(formatNode);
			}
			XAttribute attr = formatNode.Attribute("sqref");
			if (attr == null)
			{
				attr = new XAttribute("sqref", "");
				formatNode.Add(attr);
			}
			attr.Value = string.Format("{0}:{1}", startCell.CellAddress, endCell.CellAddress);

			var node = formatNode.XPathSelectElement("./d:cfRule", NameSpaceManager);
			if (node == null)
			{
				node = ExtensonMethods.NewElement("cfRule");
				formatNode.Add(node);
			}

			attr = node.Attribute("type");
			if (attr == null)
			{
				attr = new XAttribute("type", "");
				node.Add(attr);
			}
			attr.Value = "dataBar";

			attr = node.Attribute("priority");
			if (attr == null)
			{
				attr = new XAttribute("priority", "");
				node.Add(attr);
			}
			attr.Value = "1";

			// the following is poor code, but just an example!!!
			var databar = ExtensonMethods.NewElement(
				"databar",
				ExtensonMethods.NewElement("cfvo").SetAttrValue("type", "min").SetAttrValue("val", "0"),
				ExtensonMethods.NewElement("cfvo").SetAttrValue("type", "max").SetAttrValue("val", "0"),
				ExtensonMethods.NewElement("color").SetAttrValue("rgb", color)
				);
			node.Add(databar);
		}

		#region InsertRow
		/// <summary>
		/// Inserts a new row into the spreadsheet.  Existing rows below the insersion position are 
		/// shifted down.  All formula are updated to take account of the new row.
		/// </summary>
		/// <param name="position">The position of the new row</param>
		public void InsertRow(int position)
		{
			XElement rowNode = null;
			// create the new row element
			XElement rowElement = ExtensonMethods.NewElement("row").SetAttrValue("r", position.ToString());

			var sheetDataNode = WorksheetXml.XPathSelectElement("//d:sheetData", NameSpaceManager);
			if (sheetDataNode != null)
			{
				int renumberFrom = 1;
				var nodes = sheetDataNode.Nodes().Cast<XElement>().ToList();
				int nodeCount = nodes.Count;
				XElement insertAfterRowNode = null;
				int insertAfterRowNodeID = 0;
				for (int i = 0; i < nodeCount; i++)
				{
					int currentRowID = int.Parse(nodes[i].Attribute("r").Value);
					if (currentRowID < position)
					{
						insertAfterRowNode = nodes[i];
						insertAfterRowNodeID = i;
					}
					if (currentRowID >= position)
					{
						renumberFrom = currentRowID;
						break;
					}
				}

				// update the existing row ids
				for (int i = insertAfterRowNodeID + 1; i < nodeCount; i++)
				{
					int currentRowID = int.Parse(nodes[i].Attribute("r").Value);
					if (currentRowID >= renumberFrom)
					{
						nodes[i].Attribute("r").Value = Convert.ToString(currentRowID + 1);

						// now update any formula that are in the row 
						var formulaNodes = nodes[i].XPathSelectElements("./d:c/d:f", NameSpaceManager);
						foreach (var formulaNode in formulaNodes)
						{
							formulaNode.Value = ExcelCell.UpdateFormulaReferences(formulaNode.Value, 1, 0, position, 0);
						}
					}
				}

				// now insert the new row
				insertAfterRowNode?.AddAfterSelf(rowElement);
			}

			// Update stored rows
			ShiftRows(position, 1);
			Rows.Add(position, new ExcelRow(this, rowElement));
		}
		#endregion

		#region DeleteRow
		/// <summary>
		/// Deletes the specified row from the worksheet.
		/// If shiftOtherRowsUp=true then all formula are updated to take account of the deleted row.
		/// </summary>
		/// <param name="rowToDelete">The number of the row to be deleted</param>
		/// <param name="shiftOtherRowsUp">Set to true if you want the other rows renumbered so they all move up</param>
		public void DeleteRow(int rowToDelete, bool shiftOtherRowsUp)
		{
			var sheetDataNode = WorksheetXml.XPathSelectElement("//d:sheetData", NameSpaceManager);
			if (sheetDataNode != null)
			{
				var nodes = sheetDataNode.Nodes().Cast<XElement>().ToList();
				int nodeCount = nodes.Count;
				int rowNodeID = 0;
				XElement rowNode = null;
				for (int i = 0; i < nodeCount; i++)
				{
					int currentRowID = int.Parse(nodes[i].Attribute("r").Value);
					if (currentRowID == rowToDelete)
					{
						rowNodeID = i;
						rowNode = nodes[i];
					}
				}

				if (shiftOtherRowsUp)
				{
					// update the existing row ids
					for (int i = rowNodeID + 1; i < nodeCount; i++)
					{
						int currentRowID = int.Parse(nodes[i].Attribute("r").Value);
						if (currentRowID > rowToDelete)
						{
							nodes[i].Attribute("r").Value = Convert.ToString(currentRowID - 1);

							// now update any formula that are in the row 
							var formulaNodes = nodes[i].XPathSelectElements("./d:c/d:f", NameSpaceManager);
							foreach (var formulaNode in formulaNodes)
								formulaNode.Value = ExcelCell.UpdateFormulaReferences(formulaNode.Value, -1, 0, rowToDelete, 0);
						}
					}
				}
				// delete the row
				if (rowNode != null)
				{
					rowNode.Remove();
				}
			}

			// Update stored rows
			Rows.Remove(rowToDelete);
			ShiftRows(rowToDelete, -1);
		}
		#endregion

		#endregion // END Worksheet Public Methods

		#region Worksheet Private Methods

		#region Worksheet Save
		/// <summary>
		/// Saves the worksheet to the package.  For internal use only.
		/// </summary>
		protected internal void Save()  // Worksheet Save
		{
			#region Delete the printer settings component (if it exists)
			// we also need to delete the relationship from the pageSetup tag
			var pageSetup = WorksheetXml.XPathSelectElement("//d:pageSetup", NameSpaceManager);
			if (pageSetup != null)
			{
				XAttribute attr = pageSetup.Attribute(ExcelPackage.schemaRelationships+ "id");
				if (attr != null)
				{
					string relID = attr.Value;
					// first delete the attribute from the XML
					attr.Remove();

					// get the URI
					PackageRelationship relPrinterSettings = Part.GetRelationship(relID);
					Uri printerSettingsUri = new Uri("/xl" + relPrinterSettings.TargetUri.ToString().Replace("..", ""), UriKind.Relative);

					// now delete the relationship
					Part.DeleteRelationship(relPrinterSettings.Id);

					// now delete the part from the package
					xlPackage.Package.DeletePart(printerSettingsUri);
				}
			}
			#endregion

			if (_worksheetXml != null)
			{
				// save the header & footer (if defined)
				if (_headerFooter != null)
					HeaderFooter.Save();
				// replace the numeric Cell IDs we inserted with AddNumericCellIDs()
				ReplaceNumericCellIDs();

				// save worksheet to package
				PackagePart partPack = xlPackage.Package.GetPart(WorksheetUri);
				WorksheetXml.Save(partPack.GetStream(FileMode.Create, FileAccess.Write));
				xlPackage.WriteDebugFile(WorksheetXml, @"xl\worksheets", "sheet" + SheetID + ".xml");
			}
		}
		#endregion

		#region AddNumericCellIDs
		/// <summary>
		/// Adds numeric cell identifiers so that it is easier to work out position of cells
		/// Private method, for internal use only!
		/// </summary>
		private void AddNumericCellIDs()
		{
			// process each row
			foreach (var rowNode in WorksheetXml.XPathSelectElements("//d:sheetData/d:row", NameSpaceManager))
			{
				// remove the spans attribute.  Excel simply recreates it when the file is opened.
				XAttribute attr = (XAttribute)rowNode.Attribute("spans");
				if (attr != null)
				{
					attr.Remove();
				}

				int row = Convert.ToInt32(rowNode.AttributeValue("r"));
				// process each cell in current row
				foreach (var colNode in rowNode.XPathSelectElements("./d:c", NameSpaceManager))
				{
					XAttribute cellAddressAttr = (XAttribute)colNode.Attribute("r");
					if (cellAddressAttr != null)
					{
						string cellAddress = cellAddressAttr.Value;

						int col = ExcelCell.GetColumnNumber(cellAddress);
						attr = new XAttribute(tempColumnNumberTag, "");
						if (attr != null)
						{
							attr.Value = col.ToString();
							colNode.Add(attr);
							// remove all cell Addresses like A1, A2, A3 etc.
							cellAddressAttr.Remove();
						}
					}
				}
			}
		}
		#endregion

		#region ReplaceNumericCellIDs
		/// <summary>
		/// Replaces the numeric cell identifiers we inserted with AddNumericCellIDs with the traditional 
		/// A1, A2 cell identifiers that Excel understands.
		/// Private method, for internal use only!
		/// </summary>
		private void ReplaceNumericCellIDs()
		{
			int maxColumn = 0;
			// process each row
			foreach (var rowNode in WorksheetXml.XPathSelectElements("//d:sheetData/d:row", NameSpaceManager))
			{
				int row = Convert.ToInt32(rowNode.AttributeValue("r"));
				int count = 0;
				// process each cell in current row
				foreach (var colNode in rowNode.XPathSelectElements("./d:c", NameSpaceManager))
				{
					XAttribute colNumber = (XAttribute)colNode.Attribute(tempColumnNumberTag);
					if (colNumber != null)
					{
						count++;
						if (count > maxColumn) maxColumn = count;
						int col = Convert.ToInt32(colNumber.Value);
						string cellAddress = ExcelCell.GetColumnLetter(col) + row.ToString();
						XAttribute attr = new XAttribute("r", "");
						if (attr != null)
						{
							attr.Value = cellAddress;
							// the cellAddress needs to be the first attribute, otherwise Excel complains
							if (!colNode.Attributes().Any())
								colNode.Add(attr);
							else
							{
								//var firstAttr =colNode.Attributes().First();
								colNode.AddFirst(attr);
							}
						}
						// remove all numeric cell addresses added by AddNumericCellIDs
						colNumber.Remove();
					}
				}
			}

			// process each row and add the spans attribute
			// TODO: Need to add proper spans handling.
			//foreach (var rowNode in XmlDoc.SelectNodes("//d:sheetData/d:row", NameSpaceManager))
			//{
			//  // we must add or update the "spans" attribute of each row
			//  XAttribute spans = (XAttribute)rowNode.Attributes.GetNamedItem("spans");
			//  if (spans == null)
			//  {
			//    spans = XmlDoc.CreateAttribute("spans");
			//    rowNode.Add(spans);
			//  }
			//  spans.Value = "1:" + maxColumn.ToString();
			//}
		}
		#endregion

		#region Get Style Information
		/// <summary>
		/// Returns the name of the style using its xfId
		/// </summary>
		/// <param name="StyleID">The xfId of the style</param>
		/// <returns>The name of the style</returns>
		protected internal string GetStyleName(int StyleID)
		{
			string retValue = null;
			XElement styleNode = null;
			int count = 0;
			foreach (var node in xlPackage.Workbook.StylesXml.XPathSelectElements("//d:cellXfs/d:xf", NameSpaceManager))
			{
				if (count == StyleID)
				{
					styleNode = node;
					break;
				}
				count++;
			}

			if (styleNode != null)
			{
				string searchString = string.Format("//d:cellStyle[@xfId = '{0}']", styleNode.Attribute("xfId").Value);
				var styleNameNode = xlPackage.Workbook.StylesXml.XPathSelectElement(searchString, NameSpaceManager);
				if (styleNameNode != null)
				{
					retValue = styleNameNode.Attribute("name").Value;
				}
			}

			return retValue;
		}

		/// <summary>
		/// Returns the style ID given a style name.  
		/// The style ID will be created if not found, but only if the style name exists!
		/// </summary>
		/// <param name="StyleName"></param>
		/// <returns></returns>
		protected internal int GetStyleID(string StyleName)
		{
			int styleID = 0;
			// find the named style in the style sheet
			string searchString = string.Format("//d:cellStyle[@name = '{0}']", StyleName);
			var styleNameNode = xlPackage.Workbook.StylesXml.XPathSelectElement(searchString, NameSpaceManager);
			if (styleNameNode != null)
			{
				string xfId = styleNameNode.Attribute("xfId").Value;
				// look up position of style in the cellXfs 
				searchString = string.Format("//d:cellXfs/d:xf[@xfId = '{0}']", xfId);
				var styleNode = xlPackage.Workbook.StylesXml.XPathSelectElement(searchString, NameSpaceManager);
				if (styleNode != null)
				{
					var nodes = styleNode.XPathSelectElements("preceding-sibling::d:xf", NameSpaceManager);
					if (nodes != null)
						styleID = nodes.Count();
				}
			}
			return styleID;
		}
		#endregion

		#endregion  // END Worksheet Private Methods
	}  // END class Worksheet
}
