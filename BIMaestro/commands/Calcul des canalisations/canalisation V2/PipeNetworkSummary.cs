using System.Collections.Generic;
using System.Windows.Media;
using Autodesk.Revit.DB;

namespace Analyse
{
    public class PipeNetworkDisplayItem
    {
        public string Name { get; }
        public double TotalLength { get; }
        public string TotalLengthFormatted => $"{TotalLength:F2}";
        public SolidColorBrush ColorBrush { get; }
        public HashSet<ElementId> ElementIds { get; }

        public PipeNetworkDisplayItem(string name, double totalLength, SolidColorBrush colorBrush, HashSet<ElementId> elementIds)
        {
            Name = name;
            TotalLength = totalLength;
            ColorBrush = colorBrush;
            ElementIds = elementIds;
        }
    }
}