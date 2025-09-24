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
        FocusIssue,     // legacy
        FocusApply,     // Ensure3D + Focus + Zoom
        ShowAllApply,   // toggle ON/OFF
        MarkIgnored
    }

    public class ModelIssue
    {
        // Par défaut -> jamais null
        public ElementId ElementId { get; set; } = ElementId.InvalidElementId;  // élément principal (ex: MEP)
        public ElementId RelatedId { get; set; } = ElementId.InvalidElementId;  // élément lié (ex: mur traversé)
        public IssueKind Kind { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }
        public BoundingBoxXYZ BBox { get; set; }      // BB serrée (intersection si dispo)
        public bool Ignored { get; set; } = false;
    }
}
