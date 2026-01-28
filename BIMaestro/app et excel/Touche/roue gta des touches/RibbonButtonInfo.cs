using Autodesk.Revit.UI;

namespace BIMaestro.RibbonLayout
{
    public class RibbonButtonInfo
    {
        public RibbonButtonInfo(string id, string displayName, string commandClass, string imageResourceName)
        {
            Id = id;
            DisplayName = displayName;
            CommandClass = commandClass;
            ImageResourceName = imageResourceName;
        }

        public string Id { get; }
        public string DisplayName { get; set; }
        public string CommandClass { get; set; }
        public string ImageResourceName { get; set; }
    }
}
