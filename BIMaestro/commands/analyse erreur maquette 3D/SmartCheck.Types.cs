using Autodesk.Revit.DB;

namespace Analyse
{
    public enum IssueKind
    {
        WallFloating,
        WallOnWall,
        WallEmbeddedInFloor,
        MepThroughWallNoSleeve,
        MepUnconnected
    }

    public enum SmartAction
    {
        SelectOnly,
        Ensure3D,
        FocusIssue,
        ShowAllApply,   // action atomique (ON/OFF)
        MarkIgnored
    }

    public class ModelIssue
    {
        public ElementId ElementId { get; set; }      // élément principal (ex: MEP)
        public ElementId RelatedId { get; set; }      // élément lié (ex: mur traversé)
        public IssueKind Kind { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }
        public BoundingBoxXYZ BBox { get; set; }
        public bool Ignored { get; set; } = false;
    }
}
