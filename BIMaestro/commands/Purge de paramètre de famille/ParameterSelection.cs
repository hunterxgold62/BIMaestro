namespace MyRevitPlugin
{
    public class ParameterSelection
    {
        public string Name { get; set; }
        public Autodesk.Revit.DB.FamilyParameter Parameter { get; set; }
        public bool IsSelected { get; set; }
        public bool CanBeDeleted { get; set; }
        public string Group { get; set; }
    }
}
