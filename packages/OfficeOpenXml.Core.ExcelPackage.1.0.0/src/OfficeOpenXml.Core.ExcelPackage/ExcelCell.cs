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
using System.IO.Packaging;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;

namespace OfficeOpenXml.Core.ExcelPackage
{
	/// <summary>
	/// ExcelCell represents an individual worksheet cell.
	/// </summary>
	public class ExcelCell
	{
		#region Cell Private Properties
		private ExcelWorksheet _xlWorksheet;
		private XElement _cellElement;
		private int _row;
		private int _col;
		private string _value;
		private string _valueRef;
		private string _formula;
		private string _dataType;
		private Uri _hyperlink;
		#endregion

		#region ExcelCell Constructor
		/// <summary>
		/// Creates a new instance of ExcelCell class. For internal use only!
		/// </summary>
		/// <param name="xlWorksheet">A reference to the parent worksheet</param>
		/// <param name="row">The row number in the parent worksheet</param>
		/// <param name="col">The column number in the parent worksheet</param>
		protected internal ExcelCell(ExcelWorksheet xlWorksheet, int row, int col)
			: this(xlWorksheet, null, row, col) { }

		/// <summary>
		/// 
		/// </summary>
		/// <param name="xlWorksheet"></param>
		/// <param name="cellElement"></param>
		/// <param name="row"></param>
		/// <param name="col"></param>
		protected internal ExcelCell(ExcelWorksheet xlWorksheet, XElement cellElement,
			int row, int col)
		{
			if (row < 1 || col < 1)
				throw new Exception("ExcelCell Constructor: Negative row and column numbers are not allowed");
			if (xlWorksheet == null)
				throw new Exception("ExcelCell Constructor: xlWorksheet must be set to a valid reference");

			_xlWorksheet = xlWorksheet;
			_row = row;
			_col = col;

			if (cellElement == null)
			{
				cellElement = GetOrCreateCellElement(xlWorksheet, row, col);
			}
			_cellElement = cellElement;
		}
		#endregion  // END Cell Constructors

		#region ExcelCell Public Properties

		/// <summary>
		/// Read-only reference to the cell's var (for internal use only)
		/// </summary>
		protected internal XElement Element => _cellElement;

		/// <summary>
		/// Read-only reference to the cell's row number
		/// </summary>
		public int Row { get { return _row; } }
		/// <summary>
		/// Read-only reference to the cell's column number
		/// </summary>
		public int Column { get { return _col; } }
		/// <summary>
		/// Returns the current cell address in the standard Excel format (e.g. 'E5')
		/// </summary>
		public string CellAddress { get { return GetCellAddress(_row, _col); } }
		/// <summary>
		/// Returns true if the cell's contents are numeric.
		/// </summary>
		public bool IsNumeric { get { return (IsNumericValue(Value)); } }

		#region ExcelCell Value
		/// <summary>
		/// Gets/sets the value of the cell.
		/// </summary>
		public string Value
		{
			get
			{
				if (_value == null)
				{
					bool IsNumeric = true;  // default
					var valueNode = _cellElement.XPathSelectElement("./d:v", _xlWorksheet.NameSpaceManager);
					if (valueNode == null)
					{
						_valueRef = "";
						_value = "";
					}
					else
					{
						_valueRef = valueNode.Value;
						// check to see if we have a string value
						var attr = _cellElement.Attribute("t");
						if (attr != null)
							IsNumeric = attr.Value != "s";

						if (IsNumeric)
							_value = _valueRef;
						else
							_value = _xlWorksheet.xlPackage.Workbook.GetSharedString(Convert.ToInt32(_valueRef));
					}
				}
				return (_value);
			}
			set
			{
				_value = value;
				// set the value of the cell
				var valueNode = _cellElement.XPathSelectElement("./d:v", _xlWorksheet.NameSpaceManager);
				if (valueNode == null)
				{
					//  Cell with deleted value. Add a value element now.
					valueNode = ExtensonMethods.NewElement("v");
					_cellElement.Add(valueNode);
				}
				if (IsNumericValue(value))
				{
					_valueRef = value;
					// ensure we remove any existing string data type flag
					var attr = _cellElement.Attributes("t");
					if (attr != null)
						_cellElement.Attributes("t").Remove();
				}
				else
				{
					_valueRef = _xlWorksheet.xlPackage.Workbook.SetSharedString(_value).ToString();
					var attr = _cellElement.Attribute("t");
					if (attr == null)
					{
						attr = new XAttribute("t", "");
						_cellElement.Add(attr);
					}
					attr.Value = "s";
				}
				valueNode.Value = _valueRef;
			}
		}
		#endregion

		#region ExcelCell DataType
		/// <summary>
		/// Gets/sets the cell's data type.  
		/// Not currently implemented correctly!
		/// </summary>
		public string DataType
		{
			// TODO: complete DataType
			get
			{
				string retValue = null;
				var attr = _cellElement.Attribute("t");
				if (attr != null)
				{
					_dataType = "";  // default
				}
				return (retValue);
			}
			set
			{
				_dataType = value;
				var attr = _cellElement.Attribute("t");
				if (attr == null)
				{
					attr = new XAttribute("t", "");
					_cellElement.Add(attr);
				}
				attr.Value = value;
			}
		}
		#endregion

		#region ExcelCell Style
		/// <summary>
		/// Allows you to set the cell's style using a named style
		/// </summary>
		public string Style
		{
			get { return (_xlWorksheet.GetStyleName(StyleID)); }
			set { StyleID = _xlWorksheet.GetStyleID(value); }
		}

		/// <summary>
		/// Allows you to set the cell's style using the number of the style.
		/// Useful when coping styles from one cell to another.
		/// </summary>
		public int StyleID
		{
			get
			{
				int retValue = 0;
				string sid = _cellElement.AttributeValue("s");
				if (sid != "") retValue = int.Parse(sid);
				return retValue;
			}
			set { _cellElement.SetAttribute("s", value.ToString()); }
		}
		#endregion

		#region ExcelCell Hyperlink
		/// <summary>
		/// Allows you to set/get the cell's Hyperlink
		/// </summary>
		public Uri Hyperlink
		{
			get
			{
				if (_hyperlink == null)
				{
					string searchString = string.Format("//d:hyperlinks/d:hyperlink[@ref = '{0}']", CellAddress);
					var linkNode = _cellElement.Document.XPathSelectElement(searchString, _xlWorksheet.NameSpaceManager);
					if (linkNode != null)
					{
						var attr = linkNode.Attribute(ExcelPackage.schemaRelationships + "id");
						if (attr != null)
						{
							string relID = attr.Value;
							// now use the relID to lookup the hyperlink in the relationship table
							PackageRelationship relationship = _xlWorksheet.Part.GetRelationship(relID);
							_hyperlink = relationship.TargetUri;
						}
					}
				}
				return (_hyperlink);
			}
			set
			{
				_hyperlink = value;
				var linkParent = _cellElement.Document.XPathSelectElement("//d:hyperlinks", _xlWorksheet.NameSpaceManager);
				if (linkParent == null)
				{
					// create the hyperlinks node
					linkParent = ExtensonMethods.NewElement("hyperlinks");
					var prevNode = _cellElement.Document.XPathSelectElement("//d:conditionalFormatting", _xlWorksheet.NameSpaceManager);
					if (prevNode == null)
					{
						prevNode = _cellElement.Document.XPathSelectElement("//d:mergeCells", _xlWorksheet.NameSpaceManager);
						if (prevNode == null)
						{
							prevNode = _cellElement.Document.XPathSelectElement("//d:sheetData", _xlWorksheet.NameSpaceManager);
						}
					}
					prevNode.AddAfterSelf(linkParent);
				}

				string searchString = string.Format("./d:hyperlink[@ref = '{0}']", CellAddress);
				XElement linkNode = (XElement)linkParent.XPathSelectElement(searchString, _xlWorksheet.NameSpaceManager);
				if (linkNode == null)
				{
					linkNode = ExtensonMethods.NewElement("hyperLink");
					// now add cell address attribute
					linkNode.SetAttribute("ref", CellAddress);
					linkParent.Add(linkNode);
				}

				var attr = linkNode.Attribute(ExcelPackage.schemaRelationships + "id");
				if (attr == null)
				{
					attr = new XAttribute("r", ExcelPackage.schemaRelationships + "id");
					linkNode.Add(attr);
				}
				PackageRelationship relationship = null;
				string relID = attr.Value;
				if (relID == "")
					relationship = _xlWorksheet.Part.CreateRelationship(_hyperlink, TargetMode.External, @"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink");
				else
				{
					relationship = _xlWorksheet.Part.GetRelationship(relID);
					if (relationship.TargetUri != _hyperlink)
						relationship = _xlWorksheet.Part.CreateRelationship(_hyperlink, TargetMode.External, @"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink");
				}
				attr.Value = relationship.Id;

				//attr = (var)linkNode.Attributes.GetNamedItem("display", ExcelPackage.schemaMain);
				//if (attr == null)
				//{
				//  attr = _cellNode.Document.CreateAttribute("display");
				//  linkNode.Attributes.Append(attr);
				//}
				//attr.Value = Display;
			}
		}
		#endregion

		#region ExcelCell Formula
		/// <summary>
		/// Provides read/write access to the cell's formula.
		/// </summary>
		public string Formula
		{
			get
			{
				if (_formula == null)
				{
					var formulaNode = _cellElement.XPathSelectElement("./d:f", _xlWorksheet.NameSpaceManager);
					if (formulaNode != null)
					{
						// first check if we have a shared formula
						var attr = formulaNode.Attribute("t");
						if (attr == null)
						{
							// we have a standard formula
							_formula = formulaNode.Value;
						}
						else
						{
							if (attr.Value == "shared")
							{
								// we must obtain the formula from the shared cell reference
								var refAttr = formulaNode.Attribute("si");
								if (refAttr == null)
									throw new Exception("ExcelCell formula marked as shared but no reference ID found (i.e. si attribute)");
								else
								{
									string searchString = string.Format("//d:sheetData/d:row/d:c/d:f[@si='{0}']", refAttr.Value);
									var refNode = _cellElement.Document.XPathSelectElement(searchString, _xlWorksheet.NameSpaceManager);
									if (refNode == null)
										throw new Exception("ExcelCell formula marked as shared but no reference node found");
									else
										_formula = refNode.Value;
								}
							}
							else
								_formula = formulaNode.Value;
						}
					}
				}
				return (_formula);
			}
			set
			{
				// Example cell content for formulas
				// <f>D7</f>
				// <f>SUM(D6:D8)</f>
				// <f>F6+F7+F8</f>
				_formula = value;
				// insert the formula into the cell
				XElement formulaElement = (XElement)_cellElement.XPathSelectElement("./d:f", _xlWorksheet.NameSpaceManager);
				if (formulaElement == null)
				{
					formulaElement = AddFormulaElement();
				}
				// we are setting the formula directly, so remove the shared attributes (if present)
				formulaElement.Attribute(ExcelPackage.schemaMain + "t").Remove();
				formulaElement.Attribute(ExcelPackage.schemaMain + "si").Remove();

				// set the formula
				formulaElement.Value = value;

				// force Excel to re-calculate the cell by removing the value
				RemoveValue();
			}
		}
		#endregion

		#region ExcelCell Comment
		/// <summary>
		/// Returns the comment as a string
		/// </summary>
		public string Comment
		{
			// TODO: implement get which will obtain the text of the comment from the comment1.xml file
			get
			{
				throw new Exception("Function not yet implemented!");
			}
			// TODO: implement set which will add comments to the worksheet
			// this will require you to add entries to the Drawing.vml file to get this to work! 
		}
		#endregion

		// TODO: conditional formatting

		#endregion  // END Cell Public Properties

		#region ExcelCell Public Methods
		/// <summary>
		/// Removes the var that holds the cell's value.
		/// Useful when the cell contains a formula as this will force Excel to re-calculate the cell's content.
		/// </summary>
		public void RemoveValue()
		{
			var valueNode = _cellElement.XPathSelectElement("./d:v", _xlWorksheet.NameSpaceManager);
			valueNode?.Remove();
		}

		/// <summary>
		/// Returns the cell's value as a string.
		/// </summary>
		/// <returns>The cell's value</returns>
		public override string ToString() { return Value; }

		#endregion  // END Cell Public Methods

		#region ExcelCell Private Methods

		#region IsNumericValue
		/// <summary>
		/// Returns true if the string contains a numeric value
		/// </summary>
		/// <param name="Value"></param>
		/// <returns></returns>
		public static bool IsNumericValue(string Value)
		{
			Regex objNotIntPattern = new Regex("[^0-9,.-]");
			Regex objIntPattern = new Regex("^-[0-9,.]+$|^[0-9,.]+$");

			return !objNotIntPattern.IsMatch(Value) &&
							objIntPattern.IsMatch(Value);
		}
		#endregion

		#region AddFormulaNode
		/// <summary>
		/// Adds a new formula node to the cell in the correct location
		/// </summary>
		/// <returns></returns>
		protected internal XElement AddFormulaElement()
		{
			var formulaElement = ExtensonMethods.NewElement("f");
			// find the right location for insersion
			var valueNode = _cellElement.XPathSelectElement("./d:v", _xlWorksheet.NameSpaceManager);
			if (valueNode == null)
				_cellElement.Add(formulaElement);
			else
				valueNode.AddBeforeSelf(formulaElement);
			return formulaElement;
		}
		#endregion

		#region GetOrCreateCellElement
		private XElement GetOrCreateCellElement(ExcelWorksheet xlWorksheet, int row, int col)
		{
			XElement cellNode = null;
			// this will create the row if it does not already exist
			var rowNode = xlWorksheet.Row(row).Node;
			if (rowNode != null)
			{
				cellNode = (XElement)rowNode.XPathSelectElement(string.Format("./d:c[@" + ExcelWorksheet.tempColumnNumberTag + "='{0}']", col), _xlWorksheet.NameSpaceManager);
				if (cellNode == null)
				{
					//  Didn't find the cell so create the cell element
					cellNode = ExtensonMethods.NewElement("c");
					cellNode.SetAttribute(ExcelWorksheet.tempColumnNumberTag, col.ToString());

					// You must insert the new cell at the correct location.
					// Loop through the children, looking for the first cell that is
					// beyond the cell you're trying to insert. Insert before that cell.
					XElement biggerNode = null;
					var cellNodes = rowNode.XPathSelectElements("./d:c", _xlWorksheet.NameSpaceManager);
					if (cellNodes != null)
					{
						foreach (var node in cellNodes)
						{
							var colNode = node.Attribute(ExcelWorksheet.tempColumnNumberTag);
							if (colNode != null)
							{
								int colFound = Convert.ToInt32(colNode.Value);
								if (colFound > col)
								{
									biggerNode = node;
									break;
								}
							}
						}
					}
					if (biggerNode == null)
					{
						rowNode.Add(cellNode);
					}
					else
					{
						biggerNode.AddBeforeSelf(cellNode);
					}
				}
			}
			return (cellNode);
		}
		#endregion

		#endregion // END Cell Private Methods

		#region ExcelCell Static Cell Address Manipulation Routines

		#region GetColumnLetter

		/// <summary>
		/// Returns the string representation of the numbered column
		/// e.g. 1 -> A, 27 -> AA, 702 -> ZZ
		/// </summary>
		/// <param name="iColumnNumber">The number of the column</param>
		/// <returns>The letter representing the column</returns>
		public static string GetColumnLetter(int iColumnNumber)
		{
			if (iColumnNumber <= 0) { throw new ArgumentException("iColumnNumber <= 0"); }

			String name = "";
			while (iColumnNumber > 0)
			{
				int letterIndex = (iColumnNumber - 1) % 26;
				name = (char)(letterIndex + (int)'A') + name;
				iColumnNumber = (iColumnNumber - 1) / 26;
			}
			return name;
		}


		#endregion

		#region GetColumnNumber
		/// <summary>
		/// Returns the column number from the cellAddress
		/// e.g. D5 would return 5
		/// </summary>
		/// <param name="cellAddress">An Excel format cell addresss (e.g. D5)</param>
		/// <returns>The column number</returns>
		public static int GetColumnNumber(string cellAddress)
		{
			// find out position where characters stop and numbers begin
			int iColumnNumber = 0;
			int iPos = 0;
			bool found = false;
			foreach (char chr in cellAddress.ToCharArray())
			{
				iPos++;
				if (char.IsNumber(chr))
				{
					found = true;
					break;
				}
			}

			if (found)
			{
				string AlphaPart = cellAddress.Substring(0, cellAddress.Length - (cellAddress.Length + 1 - iPos));

				int length = AlphaPart.Length;
				int count = 0;
				foreach (char chr in AlphaPart.ToCharArray())
				{
					count++;
					int chrValue = ((int)chr - 64);
					switch (length)
					{
						case 1:
							iColumnNumber = chrValue;
							break;
						case 2:
							if (count == 1)
								iColumnNumber += (chrValue * 26);
							else
								iColumnNumber += chrValue;
							break;
						case 3:
							if (count == 1)
								iColumnNumber += (chrValue * 26 * 26);
							if (count == 2)
								iColumnNumber += (chrValue * 26);
							else
								iColumnNumber += chrValue;
							break;
						case 4:
							if (count == 1)
								iColumnNumber += (chrValue * 26 * 26 * 26);
							if (count == 2)
								iColumnNumber += (chrValue * 26 * 26);
							if (count == 3)
								iColumnNumber += (chrValue * 26);
							else
								iColumnNumber += chrValue;
							break;
					}
				}
			}
			return (iColumnNumber);
		}
		#endregion

		#region GetRowNumber
		/// <summary>
		/// Returns the row number from the cellAddress
		/// e.g. D5 would return 5
		/// </summary>
		/// <param name="cellAddress">An Excel format cell addresss (e.g. D5)</param>
		/// <returns>The row number</returns>
		public static int GetRowNumber(string cellAddress)
		{
			// find out position where characters stop and numbers begin
			int iPos = 0;
			bool found = false;
			foreach (char chr in cellAddress.ToCharArray())
			{
				iPos++;
				if (char.IsNumber(chr))
				{
					found = true;
					break;
				}
			}
			if (found)
			{
				string NumberPart = cellAddress.Substring(iPos - 1, cellAddress.Length - (iPos - 1));
				if (ExcelCell.IsNumericValue(NumberPart))
					return (int.Parse(NumberPart));
			}
			return (0);
		}
		#endregion

		#region GetCellAddress
		/// <summary>
		/// Returns the AlphaNumeric representation that Excel expects for a Cell Address
		/// </summary>
		/// <param name="iRow">The number of the row</param>
		/// <param name="iColumn">The number of the column in the worksheet</param>
		/// <returns>The cell address in the format A1</returns>
		public static string GetCellAddress(int iRow, int iColumn)
		{
			return (GetColumnLetter(iColumn) + iRow.ToString());
		}
		#endregion

		#region IsValidCellAddress
		/// <summary>
		/// Checks that a cell address (e.g. A5) is valid.
		/// </summary>
		/// <param name="cellAddress">The alphanumeric cell address</param>
		/// <returns>True if the cell address is valid</returns>
		public static bool IsValidCellAddress(string cellAddress)
		{
			int row = GetRowNumber(cellAddress);
			int col = GetColumnNumber(cellAddress);

			if (GetCellAddress(row, col) == cellAddress)
				return (true);
			else
				return (false);
		}
		#endregion

		#region UpdateFormulaReferences
		/// <summary>
		/// Updates the Excel formula so that all the cellAddresses are incremented by the row and column increments
		/// if they fall after the afterRow and afterColumn.
		/// Supports inserting rows and columns into existing templates.
		/// </summary>
		/// <param name="Formula">The Excel formula</param>
		/// <param name="rowIncrement">The amount to increment the cell reference by</param>
		/// <param name="colIncrement">The amount to increment the cell reference by</param>
		/// <param name="afterRow">Only change rows after this row</param>
		/// <param name="afterColumn">Only change columns after this column</param>
		/// <returns></returns>
		public static string UpdateFormulaReferences(string Formula, int rowIncrement, int colIncrement, int afterRow, int afterColumn)
		{
			string newFormula = "";

			Regex getAlphaNumeric = new Regex(@"[^a-zA-Z0-9]", RegexOptions.IgnoreCase);
			Regex getSigns = new Regex(@"[a-zA-Z0-9]", RegexOptions.IgnoreCase);

			string alphaNumeric = getAlphaNumeric.Replace(Formula, " ").Replace("  ", " ");
			string signs = getSigns.Replace(Formula, " ");
			char[] chrSigns = signs.ToCharArray();
			int count = 0;
			int length = 0;
			foreach (string cellAddress in alphaNumeric.Split(' '))
			{
				count++;
				length += cellAddress.Length;

				// if the cellAddress contains an alpha part followed by a number part, then it is a valid cellAddress
				int row = GetRowNumber(cellAddress);
				int col = GetColumnNumber(cellAddress);
				string newCellAddress = "";
				if (ExcelCell.GetCellAddress(row, col) == cellAddress)   // this checks if the cellAddress is valid
				{
					// we have a valid cell address so change its value (if necessary)
					if (row >= afterRow)
						row += rowIncrement;
					if (col >= afterColumn)
						col += colIncrement;
					newCellAddress = GetCellAddress(row, col);
				}
				if (newCellAddress == "")
				{
					newFormula += cellAddress;
				}
				else
				{
					newFormula += newCellAddress;
				}

				for (int i = length; i < signs.Length; i++)
				{
					if (chrSigns[i] == ' ')
						break;
					if (chrSigns[i] != ' ')
					{
						length++;
						newFormula += chrSigns[i].ToString();
					}
				}
			}
			return (newFormula);
		}
		#endregion

		#endregion // END CellAddress Manipulation Routines
	}
}
