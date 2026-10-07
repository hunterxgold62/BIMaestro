using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using BIMaestro.Localization;

namespace Analyse
{
    public sealed class SmartDisplayChoice
    {
        public SmartDisplayChoice(string value) { Value = value; }
        public string Value { get; }
        public override string ToString() => UiLanguage.T(Value);
    }
    public enum IssueKind
    {
        WallFloating,
        WallOnWall,
        WallEmbeddedInFloor,
        MepThroughWallNoSleeve,
        MepUnconnected,
        LinkPipeClash,
        LocalClash,
    }

    public enum SmartAction
    {
        SelectOnly,
        Ensure3D,
        FocusIssue,     // legacy
        FocusApply,     // Ensure3D + Focus + Zoom
        ShowAllApply,   // toggle ON/OFF
        MarkIgnored,
        GenerateThumbnails,
        ScanBatch,
        RefreshSetup,
        RestoreView,
        CloseSession,
        TutorialCorrect,
        ToggleContext,
        CreateReservation
    }

    public enum IssueSeverity
    {
        Info,
        Check,
        Critical
    }

    public class ModelIssue : INotifyPropertyChanged
    {
        public const string StatusActive = "Actif";
        public const string StatusToFix = "À corriger";
        public const string StatusIgnored = "À ignorer";
        public const string StatusFixed = "Traité";
        public const string StatusReview = "À revoir";

        private bool _ignored;
        private string _status = StatusActive;
        private string _statusComment;
        private string _statusUser;
        private DateTime? _statusUpdatedUtc;
        private string _thumbnailPath;
        private bool _thumbnailLoading;

        // Par défaut -> jamais null
        public long ElementIdValue => ElementId.GetIdLongValue();
        public ElementId ElementId { get; set; } = ElementId.InvalidElementId;  // élément principal (ex: MEP)
        public ElementId RelatedId { get; set; } = ElementId.InvalidElementId;  // élément lié (ex: mur traversé)
        public IssueKind Kind { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }
        public BoundingBoxXYZ BBox { get; set; }      // BB serrée (intersection si dispo)
        public string ElementCategory { get; set; }
        public string ElementTypeName { get; set; }
        public string LevelName { get; set; }
        public string LinkName { get; set; }
        public ElementId LinkedElementId { get; set; } = ElementId.InvalidElementId;
        public string ElementUniqueId { get; set; }
        public string RelatedUniqueId { get; set; }
        public string LinkUniqueId { get; set; }
        public string RelatedTypeName { get; set; }
        public string RelatedCategory { get; set; }
        public string ElementLabel { get; set; }
        public string ObstacleLabel { get; set; }
        public string ScopeDescription { get; set; }
        public bool IsApproximate { get; set; }
        public bool CanCreateReservation { get; set; }
        public double IntersectionVolumeMm3 { get; set; }
        public string Fingerprint { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        public SmartVisualScene VisualScene
        {
            get => _visualScene;
            set { if (ReferenceEquals(_visualScene, value)) return; _visualScene = value; VisualPending = false; OnPropertyChanged(); }
        }
        private SmartVisualScene _visualScene;
        [Newtonsoft.Json.JsonIgnore]
        public bool VisualPending
        {
            get => _visualPending;
            set { if (_visualPending == value) return; _visualPending = value; OnPropertyChanged(); OnPropertyChanged(nameof(VisualPlaceholder)); }
        }
        private bool _visualPending;
        public string VisualPlaceholder => UiLanguage.T(VisualPending ? "Aperçu en préparation…" : "Aperçu indisponible");
        public bool IsNew { get; set; }
        public string PairTitle => string.IsNullOrWhiteSpace(ObstacleLabel) ? ElementLabel : ElementLabel + " ↔ " + ObstacleLabel;
        public string ContextText => string.Join(" · ", new[] { LevelName, LinkName ?? "Maquette active", Category }.Where(s => !string.IsNullOrWhiteSpace(s)));
        public string ConfidenceText => UiLanguage.T(IsApproximate ? "À vérifier en 3D" : Kind == IssueKind.MepUnconnected ? "Connecteur ouvert" : "Intersection confirmée");
        public string NewText => IsNew ? UiLanguage.T("Nouveau") : string.Empty;

        public bool Ignored
        {
            get => _ignored;
            set
            {
                if (_ignored == value) return;
                _ignored = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Severity));
                OnPropertyChanged(nameof(SeverityText));
                OnPropertyChanged(nameof(SeverityDisplayText));
                OnPropertyChanged(nameof(IssueStateText));
                OnPropertyChanged(nameof(SearchText));
            }
        }

        public string Status
        {
            get => string.IsNullOrWhiteSpace(_status) ? StatusActive : _status;
            set
            {
                var next = string.IsNullOrWhiteSpace(value) ? StatusActive : value;
                if (_status == next) return;
                _status = next;
                Ignored = IsResolvedStatus(next);
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusDisplayText));
                OnPropertyChanged(nameof(IssueStateText));
                OnPropertyChanged(nameof(SearchText));
            }
        }

        public string StatusText => Status;
        public string StatusDisplayText => UiLanguage.T(StatusText);
        public string CategoryDisplayText => UiLanguage.T(Category);

        public string StatusComment
        {
            get => _statusComment;
            set
            {
                if (_statusComment == value) return;
                _statusComment = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusUpdatedText));
                OnPropertyChanged(nameof(SearchText));
            }
        }

        public string StatusUser
        {
            get => _statusUser;
            set
            {
                if (_statusUser == value) return;
                _statusUser = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusUpdatedText));
                OnPropertyChanged(nameof(SearchText));
            }
        }

        public DateTime? StatusUpdatedUtc
        {
            get => _statusUpdatedUtc;
            set
            {
                if (_statusUpdatedUtc == value) return;
                _statusUpdatedUtc = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusUpdatedText));
            }
        }

        public string ThumbnailPath
        {
            get => _thumbnailPath;
            set
            {
                if (_thumbnailPath == value) return;
                _thumbnailPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }

        public bool ThumbnailLoading
        {
            get => _thumbnailLoading;
            set
            {
                if (_thumbnailLoading == value) return;
                _thumbnailLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThumbnailStateText));
            }
        }

        public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailPath);
        public string ThumbnailStateText => ThumbnailLoading
            ? UiLanguage.T("Aperçu en cours", "Preview in Progress")
            : UiLanguage.T("Aperçu non généré", "Preview Not Generated");

        public IssueSeverity Severity
        {
            get
            {
                if (IsApproximate) return IssueSeverity.Check;
                switch (Kind)
                {
                    case IssueKind.LinkPipeClash:
                    case IssueKind.LocalClash:
                    case IssueKind.MepThroughWallNoSleeve:
                        return IssueSeverity.Critical;
                    case IssueKind.MepUnconnected:
                    case IssueKind.WallFloating:
                        return IssueSeverity.Check;
                    default:
                        return IssueSeverity.Info;
                }
            }
        }

        public string SeverityText
        {
            get
            {
                switch (Severity)
                {
                    case IssueSeverity.Critical: return "Critique";
                    case IssueSeverity.Check: return "À vérifier";
                    default: return "Info";
                }
            }
        }

        public string SeverityDisplayText => UiLanguage.T(SeverityText);

        public string VisualTitle => string.IsNullOrWhiteSpace(Category)
            ? UiLanguage.T("Anomalie 3D", "3D Issue")
            : UiLanguage.T(Category);
        public string VisualSubtitle => WhyText;
        public string IssueFamily => !string.IsNullOrWhiteSpace(ElementTypeName)
            ? ElementTypeName
            : ElementIdValue > 0 ? UiLanguage.T("Élément ", "Element ") + ElementIdValue : UiLanguage.T("Élément", "Element");
        public string RelatedLabel
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(LinkName)) return LinkName;
                return RelatedId != null && RelatedId != ElementId.InvalidElementId && RelatedId.GetIdLongValue() > 0
                    ? UiLanguage.T("Lié à ", "Related to ") + RelatedId.GetIdLongValue()
                    : string.Empty;
            }
        }

        public string IssueStateText => StatusDisplayText;

        public string StatusUpdatedText
        {
            get
            {
                if (StatusUpdatedUtc == null) return string.Empty;
                var local = StatusUpdatedUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
                return string.IsNullOrWhiteSpace(StatusUser) ? local : $"{local} - {StatusUser}";
            }
        }

        public string WhyText
        {
            get
            {
                switch (Kind)
                {
                    case IssueKind.LinkPipeClash: return UiLanguage.T("Collision entre un réseau et un fichier lié.", "Clash between a network and a linked file.");
                    case IssueKind.LocalClash: return "Intersection entre deux objets de la maquette active.";
                    case IssueKind.MepThroughWallNoSleeve: return "Le réseau intersecte une paroi. La nécessité et le dimensionnement d'une réservation restent à vérifier.";
                    case IssueKind.MepUnconnected: return UiLanguage.T("Connecteur ouvert ou réseau non raccordé.", "Open connector or unconnected network.");
                    case IssueKind.WallFloating: return UiLanguage.T("Mur sans support détecté sous sa base.", "Wall with no support detected below its base.");
                    case IssueKind.WallOnWall: return UiLanguage.T("Mur posé directement sur un autre mur.", "Wall placed directly on another wall.");
                    case IssueKind.WallEmbeddedInFloor: return UiLanguage.T("Mur noyé dans un plancher.", "Wall embedded in a floor.");
                    default: return string.IsNullOrWhiteSpace(Message) ? UiLanguage.T("Anomalie à vérifier.", "Issue to review.") : Message;
                }
            }
        }

        public string AdviceText
        {
            get
            {
                switch (Kind)
                {
                    case IssueKind.LinkPipeClash: return UiLanguage.T("Coordonner le tracé ou le lien.", "Coordinate the route or linked model.");
                    case IssueKind.LocalClash: return "Vérifier l'intersection et coordonner les deux objets.";
                    case IssueKind.MepThroughWallNoSleeve: return "Vérifier le passage, la réservation et les exigences de la paroi.";
                    case IssueKind.MepUnconnected: return UiLanguage.T("Raccorder, boucher ou confirmer le cas.", "Connect, cap, or confirm the condition.");
                    case IssueKind.WallFloating: return UiLanguage.T("Vérifier niveau, contrainte et support.", "Check the level, constraint, and support.");
                    case IssueKind.WallOnWall: return UiLanguage.T("Contrôler les contraintes verticales.", "Check the vertical constraints.");
                    case IssueKind.WallEmbeddedInFloor: return UiLanguage.T("Contrôler base, hauteur et plancher.", "Check the base, height, and floor.");
                    default: return UiLanguage.T("Ouvrir le détail avant décision.", "Open the details before deciding.");
                }
            }
        }

        public int PriorityRank
        {
            get
            {
                if (Ignored) return 90;
                switch (Severity)
                {
                    case IssueSeverity.Critical: return 0;
                    case IssueSeverity.Check: return 20;
                    default: return 50;
                }
            }
        }

        public string GroupKey => string.Join("|", new[]
        {
            Kind.ToString(),
            LevelName ?? string.Empty,
            LinkName ?? string.Empty,
            ElementCategory ?? string.Empty
        });

        public string GroupTitle
        {
            get
            {
                var suffix = string.Join(" - ", new[] { LevelName, LinkName, ElementCategory }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                return string.IsNullOrWhiteSpace(suffix) ? VisualTitle : VisualTitle + " - " + suffix;
            }
        }

        public string VisualInitials
        {
            get
            {
                switch (Kind)
                {
                    case IssueKind.LinkPipeClash: return "CL";
                    case IssueKind.MepThroughWallNoSleeve: return "TR";
                    case IssueKind.MepUnconnected: return "RO";
                    case IssueKind.WallFloating: return "MF";
                    case IssueKind.WallOnWall: return "MM";
                    case IssueKind.WallEmbeddedInFloor: return "MS";
                    default: return "3D";
                }
            }
        }

        public string SearchText => string.Join(" ", new[]
        {
            Category,
            Message,
            SeverityText,
            StatusText,
            StatusComment,
            StatusUser,
            IssueFamily,
            ElementCategory,
            ElementTypeName,
            LevelName,
            LinkName,
            ObstacleLabel,
            RelatedTypeName,
            ConfidenceText,
            RelatedLabel,
            ElementIdValue.ToString(),
            RelatedId?.GetIdLongValue().ToString()
        });

        public string IssueKey
        {
            get
            {
                var id = ElementId?.GetIdLongValue() ?? -1;
                var related = RelatedId?.GetIdLongValue() ?? -1;
                if (!string.IsNullOrWhiteSpace(ElementUniqueId))
                {
                    var a = "host:" + ElementUniqueId;
                    var b = string.IsNullOrWhiteSpace(RelatedUniqueId) ? "none" : (LinkUniqueId ?? "host") + ":" + RelatedUniqueId;
                    return "v2|" + Kind + "|" + (string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a);
                }
                return $"{Kind}|{id}|{related}";
            }
        }

        public static bool IsResolvedStatus(string status)
            => string.Equals(status, StatusFixed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, StatusIgnored, StringComparison.OrdinalIgnoreCase);

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
