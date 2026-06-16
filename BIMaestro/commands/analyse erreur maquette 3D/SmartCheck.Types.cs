using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Analyse
{
    public enum IssueKind
    {
        WallFloating,
        WallOnWall,
        WallEmbeddedInFloor,
        MepThroughWallNoSleeve,
        MepUnconnected,
        LinkPipeClash,
    }

    public enum SmartAction
    {
        SelectOnly,
        Ensure3D,
        FocusIssue,     // legacy
        FocusApply,     // Ensure3D + Focus + Zoom
        ShowAllApply,   // toggle ON/OFF
        MarkIgnored,
        GenerateThumbnails
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
        public const string StatusFixed = "OK";
        public const string StatusReview = "À revoir";

        private bool _ignored;
        private string _status = StatusActive;
        private string _statusComment;
        private string _statusUser;
        private DateTime? _statusUpdatedUtc;
        private string _thumbnailPath;
        private bool _thumbnailLoading;

        // Par défaut -> jamais null
        public int ElementIdValue => ElementId.GetIdValue();
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
                OnPropertyChanged(nameof(IssueStateText));
                OnPropertyChanged(nameof(SearchText));
            }
        }

        public string StatusText => Status;

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
        public string ThumbnailStateText => ThumbnailLoading ? "Aperçu en cours" : "Aperçu non généré";

        public IssueSeverity Severity
        {
            get
            {
                if (Ignored) return IssueSeverity.Info;
                switch (Kind)
                {
                    case IssueKind.LinkPipeClash:
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
                if (Ignored) return "OK";
                switch (Severity)
                {
                    case IssueSeverity.Critical: return "Critique";
                    case IssueSeverity.Check: return "À vérifier";
                    default: return "Info";
                }
            }
        }

        public string VisualTitle => string.IsNullOrWhiteSpace(Category) ? "Anomalie 3D" : Category;
        public string VisualSubtitle => WhyText;
        public string IssueFamily => !string.IsNullOrWhiteSpace(ElementTypeName)
            ? ElementTypeName
            : ElementIdValue > 0 ? "Élément " + ElementIdValue : "Élément";
        public string RelatedLabel
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(LinkName)) return LinkName;
                return RelatedId != null && RelatedId != ElementId.InvalidElementId && RelatedId.GetIdValue() > 0
                    ? "Lié à " + RelatedId.GetIdValue()
                    : string.Empty;
            }
        }

        public string IssueStateText => StatusText;

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
                    case IssueKind.LinkPipeClash: return "Collision entre un réseau et un fichier lié.";
                    case IssueKind.MepThroughWallNoSleeve: return "Traverse un mur sans réservation détectée.";
                    case IssueKind.MepUnconnected: return "Connecteur ouvert ou réseau non raccordé.";
                    case IssueKind.WallFloating: return "Mur sans support détecté sous sa base.";
                    case IssueKind.WallOnWall: return "Mur posé directement sur un autre mur.";
                    case IssueKind.WallEmbeddedInFloor: return "Mur noyé dans un plancher.";
                    default: return string.IsNullOrWhiteSpace(Message) ? "Anomalie à vérifier." : Message;
                }
            }
        }

        public string AdviceText
        {
            get
            {
                switch (Kind)
                {
                    case IssueKind.LinkPipeClash: return "Coordonner le tracé ou le lien.";
                    case IssueKind.MepThroughWallNoSleeve: return "Créer une réservation ou corriger le passage.";
                    case IssueKind.MepUnconnected: return "Raccorder, boucher ou confirmer le cas.";
                    case IssueKind.WallFloating: return "Vérifier niveau, contrainte et support.";
                    case IssueKind.WallOnWall: return "Contrôler les contraintes verticales.";
                    case IssueKind.WallEmbeddedInFloor: return "Contrôler base, hauteur et plancher.";
                    default: return "Ouvrir le détail avant décision.";
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
            RelatedLabel,
            ElementIdValue.ToString(),
            RelatedId?.GetIdValue().ToString()
        });

        public string IssueKey
        {
            get
            {
                var id = ElementId?.GetIdValue() ?? -1;
                var related = RelatedId?.GetIdValue() ?? -1;
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
