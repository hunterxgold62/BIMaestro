using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Grid = System.Windows.Controls.Grid;
using Panel = System.Windows.Controls.Panel;
using BIMaestro.Localization;

namespace BIMaestro.Tutorials
{
    // One attempt survives window/ribbon hand-offs; explicit exits end it.
    internal static class TutorialAnalytics
    {
        private sealed class Attempt
        {
            internal string Id = Guid.NewGuid().ToString("D");
            internal int Step;
            internal string Title;
            internal bool Ended;
        }
        private static readonly Dictionary<string, Attempt> Attempts = new Dictionary<string, Attempt>();
        internal static readonly string[] Catalog = { "reservation", "history", "colors", "pipe-calculation", "organizer", "view-template", "excel", "family-browser", "mep-booster", "clash-3d" };

        internal static void Ensure(string id)
        {
            if (!Catalog.Contains(id)) return;
            if (Attempts.TryGetValue(id, out Attempt existing) && !existing.Ended) return;
            var attempt = new Attempt();
            Attempts[id] = attempt;
            Emit(id, attempt, "started");
        }
        internal static void Step(string id, int index, string title)
        {
            Ensure(id);
            if (!Attempts.TryGetValue(id, out Attempt attempt)) return;
            attempt.Step = index + 1;
            attempt.Title = title;
            Emit(id, attempt, "step_viewed");
        }
        internal static void End(string id, string outcome)
        {
            if (!Attempts.TryGetValue(id, out Attempt attempt) || attempt.Ended) return;
            Emit(id, attempt, outcome);
            attempt.Ended = true;
        }
        internal static void Complete(string id) => End(id, "completed");
        internal static void Shutdown()
        {
            foreach (string id in Attempts.Keys.ToArray()) End(id, "interrupted");
        }
        private static void Emit(string id, Attempt attempt, string action)
        {
            Telemetry.TrackButton("Tutorial.Event", true, new {
                schema_version = 1, catalog_version = "2", tutorial_id = id,
                attempt_id = attempt.Id, event_id = Guid.NewGuid().ToString("D"),
                action, step_index = attempt.Step, step_title = attempt.Title
            });
        }
    }

    internal static class DemoTourPalette
    {
        internal static readonly Brush Accent = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 81, 50));
        internal static readonly Brush Highlight = new SolidColorBrush(System.Windows.Media.Color.FromRgb(236, 253, 245));
    }

    internal sealed class DemoStep
    {
        internal readonly string Title, Text, Target;
        internal readonly bool IsExplanation;
        internal readonly string CompletionEvent;
        internal DemoStep(string title, string text, string target, bool isExplanation = false,
            string completionEvent = null)
        { Title = title; Text = text; Target = target; IsExplanation = isExplanation;
            CompletionEvent = completionEvent; }
    }

    // New tours only add metadata and a window hook. The controls keep their native handlers.
    internal static class DemoTourCatalog
    {
        internal static IReadOnlyDictionary<string, string> EstimatedDurations => new Dictionary<string, string>
        {
            ["reservation"] = UiLanguage.Get("DemoTourService.4To6Min"),
            ["history"] = UiLanguage.Get("DemoTourService.6To10Min"),
            ["colors"] = UiLanguage.Get("DemoTourService.8To12Min"),
            ["pipe-calculation"] = UiLanguage.Get("DemoTourService.2To4Min"),
            ["organizer"] = UiLanguage.Get("DemoTourService.5To8Min"),
            ["view-template"] = UiLanguage.Get("DemoTourService.3To5Min"),
            ["excel"] = UiLanguage.Get("DemoTourService.5To8Min"),
            ["family-browser"] = UiLanguage.Get("DemoTourService.12To20Min"),
            ["mep-booster"] = UiLanguage.Get("DemoTourService.3To5Min"),
            ["clash-3d"] = UiLanguage.Get("DemoTourService.6To10Min")
        };

        internal static readonly IReadOnlyDictionary<string, string> Views = new Dictionary<string, string>
        {
            ["reservation"] = "BIMaestro - 01 Auto réservation",
            ["history"] = "BIMaestro - 02 Qui a fait ça",
            ["colors"] = "BIMaestro - 03 Couleurs et vues",
            ["pipe-calculation"] = "BIMaestro - 04 Calcul des canalisations",
            ["organizer"] = "BIMaestro - 05 Organisateur",
            ["view-template"] = "BIMaestro - 06 Gabarit source",
            ["excel"] = "BIMaestro - 08 Gestion Excel",
            ["clash-3d"] = DemoClashExercise.ViewName
        };

        internal static IReadOnlyDictionary<string, DemoStep[]> Steps => new Dictionary<string, DemoStep[]>
        {
            ["clash-3d"] = new[]
            {
                new DemoStep(UiLanguage.Get("DemoTourService.ThreePipesTwoClashes"), UiLanguage.Get("DemoTourService.TheSceneContainsAPipePassingThroughA"), "ScopeCombo", true),
                new DemoStep(UiLanguage.Get("DemoTourService.ChooseTheChecks"), UiLanguage.Get("DemoTourService.PipesAndClashesInTheModelAreSet"), "LocalCheck", true),
                new DemoStep(UiLanguage.Get("DemoTourService.AVolumeThreshold"), UiLanguage.Get("DemoTourService.Keep10MmThisThresholdIgnoresVerySmall"), "VolumeBox", true),
                new DemoStep(UiLanguage.Get("DemoTourService.CheckBothIntersections"), UiLanguage.Get("DemoTourService.ClickAnalyzeAndWaitForItToFinish"), "AnalyzeButton", completionEvent: "clash-scan-verified"),
                new DemoStep(UiLanguage.Get("DemoTourService.UnderstandTheDetails"), UiLanguage.Get("DemoTourService.TheClashBetweenTheTwoPipesIsPreselected"), "TutorialInspectButton", completionEvent: "clash-detail-opened"),
                new DemoStep(UiLanguage.Get("DemoTourService.LookAtItInRevit"), UiLanguage.Get("DemoTourService.ClickViewSelectionIn3DTheWindowWill"), "TutorialFocusButton", completionEvent: "clash-focus-observed"),
                new DemoStep(UiLanguage.Get("DemoTourService.FixTheTestPipe"), UiLanguage.Get("DemoTourService.ClickRaiseTheTestPipeOnlyTheCrossing"), "TutorialFixButton", completionEvent: "clash-corrected"),
                new DemoStep(UiLanguage.Get("DemoTourService.ProveTheClashIsGone"), UiLanguage.Get("DemoTourService.RunAnalyzeAgainWithTheSameSettingsAnd"), "AnalyzeButton", completionEvent: "clash-rescan-verified"),
                new DemoStep(UiLanguage.Get("DemoTourService.LookAtTheWallPenetration"), UiLanguage.Get("DemoTourService.TheRemainingPenetrationIsPreselectedClickViewSelection"), "TutorialFocusButton", completionEvent: "clash-wall-observed"),
                new DemoStep(UiLanguage.Get("DemoTourService.CreateTheOpening"), UiLanguage.Get("DemoTourService.ClickCreateOpeningClash3DActuallyPlacesThe"), "ReservationButton", completionEvent: "clash-reservation-created"),
                new DemoStep(UiLanguage.Get("DemoTourService.CheckAfterTheOpening"), UiLanguage.Get("DemoTourService.RunAnalyzeAgainAndWaitForItTo"), "AnalyzeButton", completionEvent: "clash-reservation-verified"),
                new DemoStep(UiLanguage.Get("DemoTourService.ExportTheBeforeAfterReport"), UiLanguage.Get("DemoTourService.KeepAllInTheStatusesAndNoSearch"), "ExportButton", completionEvent: "clash-export-written")
            },
            ["reservation"] = new[]
            {
                new DemoStep(UiLanguage.Get("DemoTourService.TheHost"), UiLanguage.Get("DemoTourService.ChooseWallTheOpeningWillBePlacedIn"), "hostMurCard"),
                new DemoStep(UiLanguage.Get("DemoTourService.TheShape"), UiLanguage.Get("DemoTourService.ChooseRectangularTheShapeDeterminesWhichDimensionParame"), "shapeRectCard"),
                new DemoStep(UiLanguage.Get("DemoTourService.TheSystem"), UiLanguage.Get("DemoTourService.ChoosePipeBIMaestroCalculatesTheOpeningAroundThe"), "objPipeCard"),
                new DemoStep(UiLanguage.Get("DemoTourService.ManualOrAutomatic"), UiLanguage.Get("DemoTourService.ManualModeLetsYouPickTheSystemAnd"), "chkAutomatique", true),
                new DemoStep(UiLanguage.Get("DemoTourService.ElementSource"), UiLanguage.Get("DemoTourService.ModelWorksOnElementsOfTheOpenProject"), "chkDoubleLink", true),
                new DemoStep(UiLanguage.Get("DemoTourService.SetUpYourFamily"), UiLanguage.Get("DemoTourService.TheFamiliesTabLetsYouUseYourOwn"), "ReservationFamiliesTab", true),
                new DemoStep(UiLanguage.Get("DemoTourService.ChooseTheRightCase"), UiLanguage.Get("DemoTourService.InFamiliesChooseHostShapeAndHostingEach"), "cbConfigSupport", true),
                new DemoStep(UiLanguage.Get("DemoTourService.FamilyAlreadyLoaded"), UiLanguage.Get("DemoTourService.IfYourTypeIsAlreadyInTheProject"), "cbLoadedType", true),
                new DemoStep(UiLanguage.Get("DemoTourService.ImportAnRFA"), UiLanguage.Get("DemoTourService.IfTheFamilyIsMissingClickBrowseChoose"), "btnBrowseRfa", true),
                new DemoStep(UiLanguage.Get("DemoTourService.MapTheDimensions"), UiLanguage.Get("DemoTourService.TheseFieldsLinkTheDimensionsCalculatedByBIMaestro"), "panelMapRectWall", true),
                new DemoStep(UiLanguage.Get("DemoTourService.VerticalPosition"), UiLanguage.Get("DemoTourService.TheVerticalReferenceSetsWhereTheFamilyIs"), "cbVerticalReference", true),
                new DemoStep(UiLanguage.Get("DemoTourService.SaveTheMapping"), UiLanguage.Get("DemoTourService.ForANewFamilySaveThisFamilyStores"), "btnApplyMapping", true),
                new DemoStep(UiLanguage.Get("DemoTourService.DimensionRounding"), UiLanguage.Get("DemoTourService.InSettingsRoundingUpToTheNext50"), "chkDefaultNorme", true),
                new DemoStep(UiLanguage.Get("DemoTourService.DynamoIsOptional"), UiLanguage.Get("DemoTourService.ADynamoScriptCanFillInParametersAfter"), "chkDefaultDynamo", true),
                new DemoStep(UiLanguage.Get("DemoTourService.BackToRun"), UiLanguage.Get("DemoTourService.YouNowUnderstandTheFamilyAndSettingsGo"), "ReservationExecutionTab", true),
                new DemoStep(UiLanguage.Get("DemoTourService.PlaceYourOpening"), UiLanguage.Get("DemoTourService.ClickRunThenSelectThePipeAndThe"), "DemoRunReservationButton")
            },
            ["pipe-calculation"] = new[]
            {
                new DemoStep(UiLanguage.Get("DemoTourService.TheScope"), UiLanguage.Get("DemoTourService.InView04BulbasaurHasSelectedTheDN100"), "CalculationOptionsTitle", true),
                new DemoStep(UiLanguage.Get("DemoTourService.IncludeDucts"), UiLanguage.Get("DemoTourService.CheckIncludeDuctsToAlsoCountTheRectangular"), "IncludeDuctsCheckBox"),
                new DemoStep(UiLanguage.Get("DemoTourService.FilterBySystem"), UiLanguage.Get("DemoTourService.TheFilterLimitsTheResultToCertainSystem"), "EnableSystemTypeFilterCheckBox", true),
                new DemoStep(UiLanguage.Get("DemoTourService.SeeTheExcelFileToo"), UiLanguage.Get("DemoTourService.KeepExportResultsToExcelCheckedAfterThe"), "ExportToExcelCheckBox"),
                new DemoStep(UiLanguage.Get("DemoTourService.RunTheCalculation"), UiLanguage.Get("DemoTourService.ClickOKReadTheSummaryInRevitThen"), "OkButton")
            },
            ["organizer"] = new[]
            {
                new DemoStep(UiLanguage.Get("DemoTourService.TwoLevelsToProcess"), UiLanguage.Get("DemoTourService.EightCMLParkingSpacesAreSelectedFourOn"), "OrganizerHeaderTitle", true),
                new DemoStep(UiLanguage.Get("DemoTourService.TheRightParameter"), UiLanguage.Get("DemoTourService.ChooseCMLNumerosDePlaceInTheList"), "ParameterComboBox"),
                new DemoStep(UiLanguage.Get("DemoTourService.Prefix"), UiLanguage.Get("DemoTourService.TypePKItWillBeAddedBeforeEach"), "PrefixTextBox"),
                new DemoStep(UiLanguage.Get("DemoTourService.Format"), UiLanguage.Get("DemoTourService.Choose001002003ToNumberTheSpaces"), "NumberFormatComboBox"),
                new DemoStep(UiLanguage.Get("DemoTourService.BandHeight"), UiLanguage.Get("DemoTourService.TheBandHeightGroupsElementsIntoRowsIn"), "BandHeightTextBox", true),
                new DemoStep(UiLanguage.Get("DemoTourService.LevelFirst"), UiLanguage.Get("DemoTourService.CheckSortByLevelTheBaseLevelWill"), "SortByLevelCheckBox"),
                new DemoStep(UiLanguage.Get("DemoTourService.FirstPass"), UiLanguage.Get("DemoTourService.ClickRenameBulbasaurWillCheckTheEightVisible"), "DemoRenameButton")
            },
            ["family-browser"] = new[]
            {
                new DemoStep(UiLanguage.Get("DemoTourService.YourTestCatalog"), UiLanguage.Get("DemoTourService.ClickFamiliesInTheLeftColumnBulbasaurHas"), "AllFamiliesButton", completionEvent: "families-root-open"),
                new DemoStep(UiLanguage.Get("DemoTourService.OpenMobilier"), UiLanguage.Get("DemoTourService.InTheTreeOpenMobilierFurnitureTheGuide"), "FolderTreeView", completionEvent: "folder-mobilier"),
                new DemoStep(UiLanguage.Get("DemoTourService.OpenBureau"), UiLanguage.Get("DemoTourService.OpenTheBureauOfficeFolderItContainsFive"), "FolderTreeView", completionEvent: "folder-bureau"),
                new DemoStep(UiLanguage.Get("DemoTourService.ReadTheCards"), UiLanguage.Get("DemoTourService.CardsShowTheNameAndAPreviewWhen"), "FamilyListView", true),
                new DemoStep(UiLanguage.Get("DemoTourService.OtherCardActions"), UiLanguage.Get("DemoTourService.OnACardRightClickOffersEditFamily"), "FamilyListView", true),
                new DemoStep(UiLanguage.Get("DemoTourService.ExploreTheSubfolder"), UiLanguage.Get("DemoTourService.ClickTheSalleDeReunionMeetingRoomCard"), "TutorialMeetingFolderCard", completionEvent: "folder-reunion"),
                new DemoStep(UiLanguage.Get("DemoTourService.SearchTheFolder"), UiLanguage.Get("DemoTourService.TypeTableInTheSearchBoxFolderMode"), "SearchBox", completionEvent: "search-table"),
                new DemoStep(UiLanguage.Get("DemoTourService.BroadenTheSearch"), UiLanguage.Get("DemoTourService.ClickAllBIMaestroClearsTheTableSearchAnd"), "SearchAllButton", completionEvent: "search-all"),
                new DemoStep(UiLanguage.Get("DemoTourService.FindAFamily"), UiLanguage.Get("DemoTourService.TypeChaiseChairTheResultsShouldNowInclude"), "SearchBox", completionEvent: "search-chaise"),
                new DemoStep(UiLanguage.Get("DemoTourService.AddAFamilyToFavorites"), UiLanguage.Get("DemoTourService.TwoChairsAreAlreadyFavoritesForTheExample"), "TutorialFavoriteStar", completionEvent: "favorite-added"),
                new DemoStep(UiLanguage.Get("DemoTourService.FindYourFavorites"), UiLanguage.Get("DemoTourService.OpenTheFavoritesTabTheStarredFamilyShould"), "FavoritesTabItem", completionEvent: "favorites-open"),
                new DemoStep(UiLanguage.Get("DemoTourService.CollectionsAndLoading"), UiLanguage.Get("DemoTourService.TheCollectionListLetsYouGroupSeveralFamilies"), "CollectionCombo", true),
                new DemoStep(UiLanguage.Get("DemoTourService.BackToFolders"), UiLanguage.Get("DemoTourService.ReopenTheFoldersTabToContinueTheExercise"), "FoldersTabItem", completionEvent: "folders-open"),
                new DemoStep(UiLanguage.Get("DemoTourService.ViewAFamilyIn3D"), UiLanguage.Get("DemoTourService.OnAFamilyCardClickThe3DButton"), "GroupedFamilyListView", completionEvent: "preview-3d"),
                new DemoStep(UiLanguage.Get("DemoTourService.OpenTheSettings"), UiLanguage.Get("DemoTourService.ClickSettingsToSeeWhereFoldersAreConfigured"), "SettingsTabItem", completionEvent: "settings-open"),
                new DemoStep(UiLanguage.Get("DemoTourService.AutomaticPictures"), UiLanguage.Get("DemoTourService.ForThisExerciseTheFiveFamiliesInSalle"), "StartPreviewButton", true, completionEvent: "preview-export-complete"),
                new DemoStep(UiLanguage.Get("DemoTourService.AdaptItToYourLibrary"), UiLanguage.Get("DemoTourService.ChangePathsLetsYouChooseYourOwnRFA"), "ChangePathsButton", true),
                new DemoStep(UiLanguage.Get("DemoTourService.DiscoverTheAIAssistant"), UiLanguage.Get("DemoTourService.ClickOpenAssistantInSmartKeywordsInThe"), "SearchMetadataAssistantButton", true, completionEvent: "search-assistant-closed"),
                new DemoStep(UiLanguage.Get("DemoTourService.LoadAndPlaceAFamily"), UiLanguage.Get("DemoTourService.BulbasaurGoesBackToTheBureauFolderThree"), "FamilyListView", completionEvent: "load-bureau-commun"),
                new DemoStep(UiLanguage.Get("DemoTourService.OpenFamilyInRevit"), UiLanguage.Get("DemoTourService.TheBrowserClosesAndBulbasaurReturnsToRevit"), "RevitFamilySplit"),
                new DemoStep(UiLanguage.Get("DemoTourService.ChooseFamilyRadialMenu"), UiLanguage.Get("DemoTourService.InTheListOpenedByTheFamilyArrow"), "RevitRosace", completionEvent: "radial-opened"),
                new DemoStep(UiLanguage.Get("DemoTourService.UnderstandRecentFamilies"), UiLanguage.Get("DemoTourService.HereTheRadialMenuShows16SampleFamilies"), "RevitUseShortcut", true),
                new DemoStep(UiLanguage.Get("DemoTourService.GoToTheViewTab"), UiLanguage.Get("DemoTourService.CloseTheRadialMenuWithEscThenClick"), "RevitViewTab"),
                new DemoStep(UiLanguage.Get("DemoTourService.OpenUserInterface"), UiLanguage.Get("DemoTourService.InViewOpenUserInterfaceInTheWindows"), "RevitUserInterface"),
                new DemoStep(UiLanguage.Get("DemoTourService.CreateTheShortcutInRevit"), UiLanguage.Get("DemoTourService.ClickKeyboardShortcutsThen1SearchForFamily"), "RevitKeyboardShortcuts", true),
                new DemoStep(UiLanguage.Get("DemoTourService.TryYourShortcut"), UiLanguage.Get("DemoTourService.GoBackToTheRevitViewPlaceThe"), "RevitUseShortcut", completionEvent: "radial-opened"),
                new DemoStep(UiLanguage.Get("DemoTourService.ShowFavoritesInTheRadialMenu"), UiLanguage.Get("DemoTourService.RightClickTheCenterOfTheRadialMenu"), "RevitUseShortcut", completionEvent: "radial-tutorial-favorites-selected"),
                new DemoStep(UiLanguage.Get("DemoTourService.UseYourShortcut"), UiLanguage.Get("DemoTourService.GoBackToARevitViewPlaceThe"), "RevitUseShortcut", completionEvent: "radial-tutorial-chaise-placed")
            },
            ["history"] = new[]
            {
                new DemoStep(UiLanguage.Get("DemoTourService.TwoDeletionsAndOneChange"), UiLanguage.Get("DemoTourService.BulbasaurDeletedTwoElementsFromTheSceneThen"), "ActionFilterCombo"),
                new DemoStep(UiLanguage.Get("DemoTourService.FilterByUser"), UiLanguage.Get("DemoTourService.ThisFilterIsolatesOnePersonSActionsIt"), "UserFilterCombo", true),
                new DemoStep(UiLanguage.Get("DemoTourService.SearchAndPeriod"), UiLanguage.Get("DemoTourService.SearchTargetsASpecificElementOrPieceOf"), "SearchBox", true),
                new DemoStep(UiLanguage.Get("DemoTourService.StartInSimpleMode"), UiLanguage.Get("DemoTourService.ChooseSimpleThisModeShowsRedVolumesTo"), "SimpleMeshModeRadio"),
                new DemoStep(UiLanguage.Get("DemoTourService.ChooseTheCluster"), UiLanguage.Get("DemoTourService.SelectTheCardContainingTheTwoDeletedFurniture"), "VisualCardsList"),
                new DemoStep(UiLanguage.Get("DemoTourService.LookAtTheSimplePreview"), UiLanguage.Get("DemoTourService.ClickVisualizeClusterTheWindowWillStepAside"), "VisualizeDeletedButton", completionEvent: "history-preview-simple-observed"),
                new DemoStep(UiLanguage.Get("DemoTourService.SwitchToDetailedMode"), UiLanguage.Get("DemoTourService.NowChooseDetailedItShowsTheFurnitureShape"), "DetailedMeshModeRadio"),
                new DemoStep(UiLanguage.Get("DemoTourService.CompareTheDetailedPreview"), UiLanguage.Get("DemoTourService.KeepTheSameClusterSelectedThenClickVisualize"), "VisualizeDeletedButton", completionEvent: "history-preview-detailed-observed"),
                new DemoStep(UiLanguage.Get("DemoTourService.RemoveTheRedPreview"), UiLanguage.Get("DemoTourService.YouHaveSeenWhereTheDeletedElementsWere"), "CleanPreviewsButton", completionEvent: "history-preview-cleaned"),
                new DemoStep(UiLanguage.Get("DemoTourService.SelectTheFurnitureToRestore"), UiLanguage.Get("DemoTourService.StayInTheVisualViewSelectTheCluster"), "VisualCardsList", true),
                new DemoStep(UiLanguage.Get("DemoTourService.BringBackBothElements"), UiLanguage.Get("DemoTourService.ClickRestoreElementsAndConfirmIfYouOnly"), "RestoreDeletedButton"),
                new DemoStep(UiLanguage.Get("DemoTourService.FindTheParameterChange"), UiLanguage.Get("DemoTourService.InActionChooseParameterChangeFindTheRow"), "ActionFilterCombo", true),
                new DemoStep(UiLanguage.Get("DemoTourService.SelectTheChange"), UiLanguage.Get("DemoTourService.InTheVisualViewSelectTheCardOf"), "VisualCardsList"),
                new DemoStep(UiLanguage.Get("DemoTourService.GoBackToThePreviousParameters"), UiLanguage.Get("DemoTourService.WithTheReferenceElementSChangeRowSelected"), "RestoreParametersButton")
            }
        };

        internal static DemoStep[] OrganizerRotatedSteps => new[]
        {
            new DemoStep(UiLanguage.Get("DemoTourService.CompareWithoutSortingByLevel"), UiLanguage.Get("DemoTourService.TheTopViewHasRotatedBy90Sort"), "SortByLevelCheckBox", true),
            new DemoStep(UiLanguage.Get("DemoTourService.RunAgainFromTheRotatedView"), UiLanguage.Get("DemoTourService.CMLNumerosDePlacePKAndThe001"), "DemoRenameButton")
        };

        // A TUTO button may be pressed from an ordinary project, without the
        // prepared model scene. These shorter tours explain the interface and
        // never claim that training elements have already been selected.
        internal static IReadOnlyDictionary<string, DemoStep[]> StandaloneSteps =>
            new Dictionary<string, DemoStep[]>
            {
                ["clash-3d"] = new[]
                {
                    new DemoStep(UiLanguage.Get("DemoTourService.DefineTheScope"), UiLanguage.Get("DemoTourService.EntireModelActiveViewOrSelectionDefinesThe"), "ScopeCombo", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.ChooseTheChecks"), UiLanguage.Get("DemoTourService.ExpandCustomizeAnalysisChooseTheRelevantCategoriesAnd"), "LocalCheck", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.UnderstandTheThreshold"), UiLanguage.Get("DemoTourService.TheThresholdIsInMmItFiltersAn"), "VolumeBox", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.ReviewTheResults"), UiLanguage.Get("DemoTourService.AnalyzeWorksInStepsAndCanBeCanceled"), "AnalyzeButton", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.CreateAnOpeningFromAClash"), UiLanguage.Get("DemoTourService.ForAConfirmedIntersectionBetweenAnElementAnd"), "ResultsList", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.FixThenCheck"), UiLanguage.Get("DemoTourService.FixTheElementsWithRevitToolsThenRun"), "ResultsList", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.Export"), UiLanguage.Get("DemoTourService.ExportProducesAStandaloneHTMLReportOrA"), "ExportButton", true)
                },
                ["reservation"] = new[]
                {
                    new DemoStep(UiLanguage.Get("DemoTourService.ChooseTheHost"), UiLanguage.Get("DemoTourService.WallCreatesAnOpeningInACrossedWall"), "hostMurCard", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.ChooseTheShapeAndTheSystem"), UiLanguage.Get("DemoTourService.TheShapeAndHostDetermineTheDimensionsTo"), "shapeRectCard", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.SetUpYourFamily2"), UiLanguage.Get("DemoTourService.TheFamiliesTabLinksAnRFATypeTo"), "ReservationFamiliesTab", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.ChooseTheSources"), UiLanguage.Get("DemoTourService.ModelUsesLocalElementsAndHostsIFCLink"), "chkDoubleLink", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.ManualOrAutomatic2"), UiLanguage.Get("DemoTourService.TheAutomaticScanIsLimitedToWallsAnd"), "chkAutomatique", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.RunItInYourProject"), UiLanguage.Get("DemoTourService.ClickRunSelectTheRequestedElementsThenCheck"), "DemoRunReservationButton", true)
                },
                ["pipe-calculation"] = new[]
                {
                    new DemoStep(UiLanguage.Get("DemoTourService.DefineTheScope"), UiLanguage.Get("DemoTourService.SelectThePipesAndFittingsToCalculateIn"), "CalculationOptionsTitle", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.IncludeDucts"), UiLanguage.Get("DemoTourService.CheckThisOptionIfYourSelectionAlsoContains"), "IncludeDuctsCheckBox", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.ReadAndExport"), UiLanguage.Get("DemoTourService.KeepExcelExportCheckedToFirstSeeThe"), "ExportToExcelCheckBox", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.Calculate"), UiLanguage.Get("DemoTourService.ClickOKWhenYourSelectionAndOptionsAre"), "OkButton", true)
                },
                ["organizer"] = new[]
                {
                    new DemoStep(UiLanguage.Get("DemoTourService.ChooseTheElements"), UiLanguage.Get("DemoTourService.OrganizerWorksOnTheElementsSelectedInThe"), "ParameterComboBox", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.ChooseTheParameter"), UiLanguage.Get("DemoTourService.SelectTheInstanceParameterToChangeOnThe"), "ParameterComboBox", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.SetTheName"), UiLanguage.Get("DemoTourService.ThePrefixFormatAndReadingDirectionDetermineThe"), "PrefixTextBox", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.Apply"), UiLanguage.Get("DemoTourService.RenameWritesTheValuesToTheProjectTo"), "DemoRenameButton", true)
                },
                ["history"] = new[]
                {
                    new DemoStep(UiLanguage.Get("DemoTourService.FindActions"), UiLanguage.Get("DemoTourService.FilterTheHistoryByActionTypeUserOr"), "ActionFilterCombo", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.ExamineAnElement"), UiLanguage.Get("DemoTourService.SelectACardInTheVisualViewTo"), "VisualCardsList", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.ChooseThePreview"), UiLanguage.Get("DemoTourService.SimpleUsesVolumesToLocateDeletedElementsDetailed"), "SimpleMeshModeRadio", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.Visualize"), UiLanguage.Get("DemoTourService.VisualizeClusterShowsAPreviewOfTheSelected"), "VisualizeDeletedButton", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.CleanThePreview"), UiLanguage.Get("DemoTourService.AfterLookingAtTheElementsInRevitClick"), "CleanPreviewsButton", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.RestoreDeletedElements"), UiLanguage.Get("DemoTourService.RestoreElementsRecreatesDeletedElementsCheckTheDeletion"), "RestoreDeletedButton", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.RestoreParameters"), UiLanguage.Get("DemoTourService.ForAParameterChangeSelectTheMatchingEvent"), "RestoreParametersButton", true)
                }
            };

    }

    internal static class DemoHistoryScene
    {
        internal const string ChairCountParameter = "Nombre chaises";
        internal static Parameter ChairCount(FamilyInstance instance)
        {
            Parameter parameter = instance?.LookupParameter(ChairCountParameter);
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.Integer)
                throw new InvalidOperationException(UiLanguage.Get("DemoTourService.CreateANewModelWithTheUpdatedFamily"));
            return parameter;
        }
        internal static string ModifiedWitnessMark => DemoProjectBuilder.HistoryFurnitureMark(0) + "_MODIFIE";

        private static bool IsLearningDocument(Document doc) =>
            doc != null && System.IO.Path.GetFileName(doc.PathName)
                .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase);

        private static FamilyInstance FindWitness(IEnumerable<FamilyInstance> furniture) =>
            furniture.FirstOrDefault(instance =>
            {
                string mark = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                return mark == DemoProjectBuilder.HistoryFurnitureMark(0) || mark == ModifiedWitnessMark;
            });

        internal static bool HasRestoredFurniture(Document doc)
        {
            if (!IsLearningDocument(doc)) return false;
            var tables = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().Where(instance => instance.SuperComponent == null &&
                    instance.Symbol?.Family?.Name == "CML_Table ronde + chaise").ToList();
            // Reconstruction deliberately omits Mark; identify the restored parent tables
            // by their family and exercise positions instead of expecting copied marks.
            return Enumerable.Range(1, 2).All(index => tables.Any(instance =>
                (instance.Location as LocationPoint)?.Point.DistanceTo(DemoProjectBuilder.HistoryFurniturePosition(index)) <
                    UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters)));

        }

        internal static bool IsNestedExerciseDeletion(Document doc, Analyse.ElementHistoryEvent item)
        {
            return IsLearningDocument(doc) && item?.Action == "delete" &&
                (item.Tx ?? "").StartsWith("BIMaestro - Exercice historique", StringComparison.Ordinal) &&
                item.Delta != null && item.Delta.TryGetValue("superComponentUniqueId", out object parent) &&
                !string.IsNullOrWhiteSpace(Convert.ToString(parent));
        }

        internal static bool HasRestoredParameters(Document doc)
        {
            if (!IsLearningDocument(doc)) return false;
            FamilyInstance witness = FindWitness(new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>());
            return witness != null &&
                witness.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ==
                    DemoProjectBuilder.HistoryFurnitureMark(0) &&
                witness.LookupParameter(ChairCountParameter)?.AsInteger() == 4;
        }

        internal static int Reset(Document doc, out int removedReservations)
        {
            removedReservations = 0;
            if (doc == null || !System.IO.Path.GetFileName(doc.PathName)
                    .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(UiLanguage.Get("DemoTourService.FirstOpenABIMaestroApprentissageModelToRestart"));

            var furniture = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().ToList();
            FamilyInstance witness = FindWitness(furniture);
            if (witness == null)
                throw new InvalidOperationException(UiLanguage.Get("DemoTourService.ThisModelDoesNotHaveTheFurnitureScene"));
            Level level = doc.GetElement(witness.LevelId) as Level;
            if (level == null) throw new InvalidOperationException(UiLanguage.Get("DemoTourService.TheLevelOfTheDemoFurnitureCannotBe"));

            var reservations = furniture.Where(instance =>
                (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                == DemoProjectBuilder.DemoPrefix + "RESERVATION_CREEE")
                .Select(instance => instance.Id).ToList();
            int restoredFurniture = 0;
            using (var tx = new Transaction(doc, "BIMaestro - Recommencer les exercices"))
            {
                tx.Start();
                witness.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(DemoProjectBuilder.HistoryFurnitureMark(0));
                ChairCount(witness).Set(4);
                if (reservations.Count > 0) doc.Delete(reservations);
                removedReservations = reservations.Count;

                for (int index = 1; index <= 2; index++)
                {
                    XYZ target = DemoProjectBuilder.HistoryFurniturePosition(index);
                    string mark = DemoProjectBuilder.HistoryFurnitureMark(index);
                    FamilyInstance instance = furniture.FirstOrDefault(candidate =>
                        candidate.Id != witness.Id && !reservations.Contains(candidate.Id) &&
                        (candidate.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "") == mark);
                    if (instance == null)
                        instance = furniture.FirstOrDefault(candidate =>
                            candidate.Id != witness.Id && !reservations.Contains(candidate.Id) &&
                            candidate.GetTypeId().Equals(witness.GetTypeId()) &&
                            (candidate.Location as LocationPoint)?.Point.DistanceTo(target) <
                                UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters));
                    if (instance == null)
                    {
                        instance = doc.Create.NewFamilyInstance(target, witness.Symbol, level,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        restoredFurniture++;
                    }
                    else if (instance.Location is LocationPoint point)
                        ElementTransformUtils.MoveElement(doc, instance.Id, target - point.Point);

                    Parameter parameter = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (parameter != null && !parameter.IsReadOnly) parameter.Set(mark);
                    ChairCount(instance).Set(4);
                }
                tx.Commit();
            }
            doc.Save();
            return restoredFurniture;
        }

        internal static int Prepare(Document doc)
        {
            // Only touch the generated learning model, and only its marked furniture.
            if (doc == null || !System.IO.Path.GetFileName(doc.PathName)
                    .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(UiLanguage.Get("DemoTourService.FirstOpenABIMaestroApprentissageModelToPrepare"));

            var furniture = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().ToList();
            FamilyInstance witness = FindWitness(furniture);
            if (witness == null)
                throw new InvalidOperationException(UiLanguage.Get("DemoTourService.ThisModelUsesTheOldScenarioCreateA"));
            var toRemove = furniture
                .Where(instance => (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith(DemoProjectBuilder.DemoPrefix + "HISTORIQUE_A_RESTAURER_", StringComparison.Ordinal))
                .Select(instance => instance.Id).ToList();
            Parameter chairCount = ChairCount(witness);

            // Normalize the exercise before taking the deletion/parameter snapshots.
            using (var tx = new Transaction(doc, "BIMaestro - État initial du mobilier témoin"))
            {
                tx.Start();
                chairCount.Set(4);
                tx.Commit();
            }
            Analyse.ElementHistoryTracker.FlushPendingForHistory();
            Analyse.ElementHistoryTracker.ScheduleDeferredPrime(doc);
            Analyse.ElementHistoryTracker.CaptureSelectedElementDetails(doc, toRemove.Concat(new[] { witness.Id }).ToList());
            if (chairCount.AsInteger() != 4)
                throw new InvalidOperationException(UiLanguage.Get("DemoTourService.TheReferenceFurnitureMustHave4ChairsBefore"));
            using (var tx = new Transaction(doc, "BIMaestro - Exercice historique : supprimer le mobilier"))
            {
                tx.Start();
                if (toRemove.Count > 0) doc.Delete(toRemove);
                tx.Commit();
            }
            Analyse.ElementHistoryTracker.FlushPendingForHistory();
            DateTime changeStartedUtc = DateTime.UtcNow;
            // A separate transaction records both before/after values on the surviving instance.
            using (var tx = new Transaction(doc, "BIMaestro - Exercice historique : modifier les paramètres du témoin"))
            {
                tx.Start();
                chairCount.Set(2);
                tx.Commit();
            }
            if (ChairCount(witness).AsInteger() != 2)
                throw new InvalidOperationException(UiLanguage.Get("DemoTourService.TheReferenceFurnitureDidNotKeepTheChange"));
            Analyse.ElementHistoryTracker.FlushPendingForHistory();
            doc.Save();
            bool recorded = Analyse.ElementHistoryTracker.LoadElementHistory(
                Analyse.ElementHistoryTracker.GetDocumentKeysForHistory(doc), witness.UniqueId, 20)
                .Any(item => item.Ts >= changeStartedUtc && item.Action == "param_change" &&
                    item.Delta != null && item.Delta.TryGetValue("parameters", out object values) &&
                    Newtonsoft.Json.Linq.JArray.FromObject(values).OfType<Newtonsoft.Json.Linq.JObject>().Any(change =>
                        string.Equals((string)(change["Name"] ?? change["name"]), ChairCountParameter, StringComparison.OrdinalIgnoreCase) &&
                        (string)(change["OldValue"] ?? change["oldValue"]) == "4" &&
                        (string)(change["NewValue"] ?? change["newValue"]) == "2"));
            if (!recorded)
                throw new InvalidOperationException(UiLanguage.Get("DemoTourService.TheNombreChaisesChange42WasNot"));
            return toRemove.Count;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class DemoToursCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "DemoTours";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
            => ShowChoice(data.Application, ref message);

        internal static Result ShowChoice(UIApplication app, ref string message)
        {
            if (!DemoProjectBuilder.IsLearningDocument(app.ActiveUIDocument?.Document))
                return Result.Cancelled;
            var choice = new DemoTrainingChoiceWindow();
            new WindowInteropHelper(choice).Owner = app.MainWindowHandle;
            choice.ShowDialog();
            return RunChoice(app, choice.SelectedTour, ref message);
        }

        internal static Result RunChoice(UIApplication uiApp, string selectedChoice, ref string message)
        {
            if (selectedChoice == "create")
            {
                try
                {
                    DemoProjectBuilder.Create(uiApp);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurNeedsHelp"),
                        UiLanguage.Get("DemoTourService.UnableToCreate") + ex.Message);
                    return Result.Failed;
                }
            }
            else if (!string.IsNullOrEmpty(selectedChoice))
            {
                UIDocument activeDocument = uiApp.ActiveUIDocument;
                if (activeDocument == null)
                {
                    DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurIsWaitingForAModel"),
                        UiLanguage.Get("DemoTourService.FirstOpenTheTrainingModelOrARevit"));
                    return Result.Cancelled;
                }
                if (selectedChoice == "reset")
                {
                    try
                    {
                        int restored = DemoHistoryScene.Reset(activeDocument.Document, out int removed);
                        DemoProjectBuilder.UpdateTrainingViews(activeDocument.Document);
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.WellDoneExercisesReady"),
                            UiLanguage.Get("DemoTourService.ExercisesReadyToRestart") + restored + UiLanguage.Get("DemoTourService.FurnitureItemSPutBackAnd") +
                            removed + UiLanguage.Get("DemoTourService.WalkthroughOpeningSRemovedYourPersonalColorsAre"));
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurNeedsHelp"),
                            UiLanguage.Get("DemoTourService.UnableToReset") + ex.Message);
                        return Result.Failed;
                    }
                }
                DemoProjectBuilder.UpdateTrainingViews(activeDocument.Document);
                if (selectedChoice == "clash-3d")
                {
                    try
                    {
                        View3D view = DemoClashExercise.Prepare(activeDocument.Document);
                        activeDocument.Selection.SetElementIds(DemoClashExercise.Sources(activeDocument.Document));
                        if (activeDocument.ActiveView.Id != view.Id)
                        {
                            activeDocument.RequestViewChange(view);
                            DemoTourPendingSelection.Begin(activeDocument.Document, view.Id, selectedChoice);
                            return Result.Succeeded;
                        }
                        return DemoTourPendingSelection.PrepareNow(uiApp, selectedChoice) ? Result.Succeeded : Result.Cancelled;
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurIsPreparingClash3D"), ex.Message);
                        return Result.Failed;
                    }
                }
                if (selectedChoice == "mep-booster")
                {
                    try
                    {
                        View3D view = DemoProjectBuilder.EnsureBoosterScene(activeDocument.Document);
                        activeDocument.Selection.SetElementIds(new List<ElementId>());
                        activeDocument.RequestViewChange(view);
                        DemoBoosterExercise.Begin(uiApp);
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurNeedsHelp2"), UiLanguage.Get("DemoTourService.UnableToPrepareMEPBooster") + ex.Message);
                        return Result.Failed;
                    }
                }
                if (selectedChoice == "excel")
                {
                    try
                    {
                        ViewSchedule schedule = DemoProjectBuilder.EnsureExcelScene(activeDocument.Document);
                        DemoExcelExercise.Begin(activeDocument.Document, schedule);
                        if (activeDocument.ActiveView.Id != schedule.Id)
                            activeDocument.RequestViewChange(schedule);
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurNeedsHelp"),
                            UiLanguage.Get("DemoTourService.UnableToPrepareExcelManager") + ex.Message);
                        return Result.Failed;
                    }
                }
                if (selectedChoice == "pipe-calculation")
                    DemoCalculationExercise.Begin(activeDocument.Document);
                if (selectedChoice == "organizer")
                {
                    try
                    {
                        DemoProjectBuilder.EnsureOrganizerScene(activeDocument.Document);
                        DemoProjectBuilder.ResetOrganizerViewOrientation(activeDocument.Document);
                        DemoOrganizerExercise.Begin(activeDocument.Document);
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurNeedsHelp"),
                            UiLanguage.Get("DemoTourService.UnableToPrepareOrganizer") + ex.Message);
                        return Result.Failed;
                    }
                }
                if (DemoTourCatalog.Views.TryGetValue(selectedChoice, out string viewName))
                {
                    View tourView = new FilteredElementCollector(activeDocument.Document)
                        .OfClass(typeof(View3D)).Cast<View3D>()
                        .FirstOrDefault(view => view.Name == viewName);
                    if (tourView == null && (selectedChoice == "pipe-calculation" ||
                        selectedChoice == "organizer" || selectedChoice == "view-template"))
                    {
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurIsLookingForTheRightView"),
                            UiLanguage.Get("DemoTourService.ThisModelDoesNotContainTheSceneFor"));
                        return Result.Cancelled;
                    }
                    if (tourView != null && activeDocument.ActiveView.Id != tourView.Id)
                    {
                        if (selectedChoice == "view-template") activeDocument.ActiveView = tourView;
                        else activeDocument.RequestViewChange(tourView);
                        if (selectedChoice == "pipe-calculation" || selectedChoice == "organizer")
                        {
                            DemoTourPendingSelection.Begin(activeDocument.Document, tourView.Id, selectedChoice);
                            return Result.Succeeded;
                        }
                    }
                }
                if (selectedChoice == "pipe-calculation" || selectedChoice == "organizer")
                {
                    return DemoTourPendingSelection.PrepareNow(uiApp, selectedChoice)
                        ? Result.Succeeded : Result.Cancelled;
                }
                if (selectedChoice == "view-template")
                {
                    try { DemoViewTemplateGuide.Begin(activeDocument.Document); }
                    catch (Exception ex) { message = ex.ToString(); DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.ViewTemplate"), UiLanguage.Get("DemoTourService.UnableToPrepare") + ex.Message); return Result.Failed; }
                    DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.WellDoneViewTemplate"),
                        UiLanguage.Get("DemoTourService.LookAtTheSourceViewRedPipesAnd"));
                }
                if (selectedChoice == "history")
                {
                    try
                    {
                        int removed = DemoHistoryScene.Prepare(activeDocument.Document);
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurIsPreparingTheInvestigation"), removed > 0
                            ? removed + UiLanguage.Get("DemoTourService.ElementsWereDeletedThenTheThirdWentFrom")
                            : UiLanguage.Get("DemoTourService.TheDeletionsAreAlreadyPreparedAndTheReference"));
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurNeedsHelp"),
                            UiLanguage.Get("DemoTourService.UnableToPrepareTheExercise") + ex.Message);
                        return Result.Failed;
                    }
                }
                Couleur.AppearanceOnboarding.StartIntro(uiApp.MainWindowHandle, selectedChoice);
            }
            return Result.Succeeded;
        }
    }

    internal static class DemoTourPendingSelection
    {
        private static string _documentPath;
        private static ElementId _viewId;
        private static string _tourId;
        private static DateTime _deadlineUtc;

        internal static void Begin(Document document, ElementId viewId, string tourId)
        {
            _documentPath = document.PathName;
            _viewId = viewId;
            _tourId = tourId;
            _deadlineUtc = DateTime.UtcNow.AddSeconds(30);
        }

        internal static bool ProcessIdling(UIApplication app)
        {
            if (_tourId == null) return false;
            UIDocument uiDocument = app?.ActiveUIDocument;
            if (uiDocument == null || !string.Equals(uiDocument.Document.PathName,
                _documentPath, StringComparison.OrdinalIgnoreCase))
            {
                Clear();
                return false;
            }
            if (!uiDocument.ActiveView.Id.Equals(_viewId))
            {
                if (DateTime.UtcNow < _deadlineUtc) return true;
                Clear();
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurIsWaitingForTheView"),
                    UiLanguage.Get("DemoTourService.RevitDidNotActivateTheTrainingViewOpen"));
                return true;
            }
            string tourId = _tourId;
            Clear();
            PrepareNow(app, tourId);
            return true;
        }

        internal static bool PrepareNow(UIApplication app, string tourId)
        {
            UIDocument uiDocument = app.ActiveUIDocument;
            string marker = DemoProjectBuilder.DemoPrefix +
                (tourId == "clash-3d" ? "CLASH_" : tourId == "organizer" ? "ORGANISATEUR_" : "CALCUL_");
            var ids = new FilteredElementCollector(uiDocument.Document)
                .WhereElementIsNotElementType()
                .Where(element => (tourId != "clash-3d" || element is Autodesk.Revit.DB.Plumbing.Pipe) && (element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith(marker, StringComparison.Ordinal))
                .Select(element => element.Id).ToList();
            if (ids.Count == 0)
            {
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurIsLookingForTheScene"),
                    UiLanguage.Get("DemoTourService.TheElementsForThisExerciseAreMissingCreate"));
                return false;
            }
            uiDocument.Selection.SetElementIds(ids);
            if (tourId == "organizer")
                try { uiDocument.ShowElements(ids); } catch { /* La sélection reste active. */ }
            DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.WellDoneSelectionReady"),
                ids.Count + (tourId == "organizer"
                    ? UiLanguage.Get("DemoTourService.SpacesOnTwoLevelsAreSelectedFollowBulbasaur")
                    : UiLanguage.Get("DemoTourService.ElementsInTheViewAreSelectedFollowBulbasaur")));
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, tourId);
            return true;
        }

        private static void Clear()
        {
            _documentPath = null;
            _viewId = null;
            _tourId = null;
            _deadlineUtc = DateTime.MinValue;
        }
    }

    internal static class DemoCalculationExercise
    {
        private static string _documentPath;
        private static bool _active;

        internal static void Begin(Document document)
        {
            DemoProjectBuilder.EnsureCalculationValves(document);
            _documentPath = document.PathName;
            _active = true;
        }

        internal static void Verify(UIApplication app, ICollection<ElementId> selectedIds,
            bool includeDucts, bool exportToExcel, string excelFilePath,
            Dictionary<double, double> pipeLengths,
            Dictionary<string, double> ductLengths, Dictionary<string, double> ductFittingLengths,
            Dictionary<string, int> elbowCounts, Dictionary<string, int> pipeAccessoryCounts)
        {
            UIDocument uiDocument = app.ActiveUIDocument;
            if (!_active || uiDocument == null ||
                !string.Equals(uiDocument.Document.PathName, _documentPath, StringComparison.OrdinalIgnoreCase))
                return;

            var demoIds = DemoExerciseElements.Find(uiDocument.Document, "CALCUL_");
            bool completeSelection = demoIds.Count >= 12 &&
                demoIds.All(id => selectedIds.Contains(id));
            bool bothDiameters = pipeLengths.Any(pair => Math.Abs(pair.Key - 100) < 1 && pair.Value > 0) &&
                pipeLengths.Any(pair => Math.Abs(pair.Key - 50) < 1 && pair.Value > 0);
            int elbowCount = elbowCounts.Values.Sum();
            int valveCount = pipeAccessoryCounts.Where(pair => pair.Key == "Vanne papillon - 50-300 mm")
                .Sum(pair => pair.Value);
            if (!completeSelection || !bothDiameters || elbowCount < 5 || valveCount < 2)
            {
                Retry(app, uiDocument, UiLanguage.Get("DemoTourService.TheResultMustContainBothDiametersTheirFive"));
                return;
            }

            double ductTotal = ductLengths.Values.Sum();
            double ductFittingsTotal = ductFittingLengths.Values.Sum();
            if (!includeDucts || ductTotal <= 0 || ductFittingLengths.Count == 0)
            {
                Retry(app, uiDocument, UiLanguage.Get("DemoTourService.TheResultMustContainTheDuctsAndTheir"));
                return;
            }
            if (!exportToExcel || string.IsNullOrEmpty(excelFilePath) ||
                !System.IO.File.Exists(excelFilePath))
            {
                Retry(app, uiDocument, UiLanguage.Get("DemoTourService.TheExcelFileIsMissingKeepExportResults"));
                return;
            }
            _active = false;
            Reselect(uiDocument, demoIds);
            DemoTourCompletion.Show(app.MainWindowHandle, "pipe-calculation",
                UiLanguage.Format("DemoTourService.BulbasaurCheckedTheDN100AndDN50PipesTheir", (object)elbowCount, (object)valveCount, (object)ductTotal, (object)ductFittingsTotal, (object)excelFilePath));
        }

        private static void Retry(UIApplication app, UIDocument uiDocument, string message)
        {
            Reselect(uiDocument, DemoExerciseElements.Find(uiDocument.Document, "CALCUL_"));
            DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurNeedsAnotherTry"), message);
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "pipe-calculation");
        }

        private static void Reselect(UIDocument uiDocument, List<ElementId> ids)
        {
            if (ids.Count > 0) uiDocument.Selection.SetElementIds(ids);
        }
    }

    internal static class DemoExerciseElements
    {
        internal static List<ElementId> Find(Document document, string suffix)
        {
            string marker = DemoProjectBuilder.DemoPrefix + suffix;
            return new FilteredElementCollector(document).WhereElementIsNotElementType()
                .Where(element => (element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith(marker, StringComparison.Ordinal))
                .Select(element => element.Id).ToList();
        }

    }

    internal static class DemoExcelExercise
    {
        private static string _documentPath;
        private static ElementId _scheduleId;
        private static ElementId _parkingViewId;
        private static string _workbookPath;
        private static string _fieldName;
        private static string _referenceFieldName;
        private static Dictionary<string, string> _initialNumbers;
        private static Dictionary<string, string> _references;
        private static Dictionary<string, string> _expectedNumbers;
        private static string _verifiedChanges;
        private static int _stage; // 1 export, 2 edit/import, 3 verified
        private static int _pendingAction; // 1 introduction, 2 focus the edited places
        private static DateTime _deadlineUtc;

        internal static bool IsActive(Document document) => _stage > 0 && document != null &&
            string.Equals(document.PathName, _documentPath, StringComparison.OrdinalIgnoreCase);
        internal static bool NeedsExport(Document document) => IsActive(document) && _stage == 1;
        internal static bool NeedsImport(Document document) => IsActive(document) && _stage == 2;

        internal static void Begin(Document document, ViewSchedule schedule)
        {
            var places = DemoProjectBuilder.FindExcelParking(document);
            if (places.Count != 4)
                throw new InvalidOperationException(UiLanguage.Get("DemoTourService.TheFourSpacesOfTheExcelWalkthroughCannot"));
            _fieldName = DemoProjectBuilder.ExcelValueField(schedule);
            _referenceFieldName = DemoProjectBuilder.ExcelReferenceField(schedule);
            using (var tx = new Transaction(document, "BIMaestro - Synchroniser les numéros visibles"))
            {
                tx.Start();
                DemoProjectBuilder.SynchronizeExcelParkingNumbers(places, _fieldName,
                    recoverLegacyValue: true);
                tx.Commit();
            }
            _documentPath = document.PathName;
            _scheduleId = schedule.Id;
            _parkingViewId = new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(view => view.Name == "BIMaestro - 08 Parking Excel")?.Id;
            _workbookPath = null;
            _references = places.ToDictionary(place => place.UniqueId,
                place => place.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "");
            _initialNumbers = places.ToDictionary(place => place.UniqueId,
                place => DemoProjectBuilder.ExcelValueParameter(place, _fieldName)?.AsString() ?? "");
            _expectedNumbers = null;
            _verifiedChanges = null;
            _stage = 1;
            _pendingAction = 1;
            _deadlineUtc = DateTime.UtcNow.AddSeconds(30);
        }

        internal static bool ProcessIdling(UIApplication app)
        {
            if (_pendingAction == 0) return false;
            UIDocument uiDocument = app?.ActiveUIDocument;
            if (uiDocument == null || !IsActive(uiDocument.Document)) return false;
            ElementId expected = _pendingAction == 1 ? _scheduleId : _parkingViewId;
            if (expected == null || !uiDocument.ActiveView.Id.Equals(expected))
            {
                if (DateTime.UtcNow < _deadlineUtc) return true;
                _pendingAction = 0;
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurIsWaitingForTheRightView"),
                    UiLanguage.Get("DemoTourService.TheExcelWalkthroughViewDidNotOpenRestart"));
                return true;
            }
            int action = _pendingAction;
            _pendingAction = 0;
            if (action == 1)
            {
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.WellDoneExcelManager"),
                    UiLanguage.Get("DemoTourService.HereIsTheScheduleOfTheFourSpaces") + _fieldName + UiLanguage.Get("DemoTourService.ContainsTheEditableNumberVisibleOnEachSpace") +
                    _referenceFieldName + UiLanguage.Get("DemoTourService.IsTheFixedReferenceDoNotChangeIt"));
                Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "excel");
            }
            else
            {
                var ids = DemoProjectBuilder.FindExcelParking(uiDocument.Document)
                    .Select(place => place.Id).ToList();
                uiDocument.Selection.SetElementIds(ids);
                try { uiDocument.ShowElements(ids); } catch { }
                DemoTourCompletion.Show(app.MainWindowHandle, "excel",
                    UiLanguage.Get("DemoTourService.BulbasaurRereadTheFourSpacesInRevitVerified") + _verifiedChanges +
                    UiLanguage.Get("DemoTourService.TheReference") + _referenceFieldName + UiLanguage.Get("DemoTourService.StayedTheSameTheChangedNumberIsAlso"));
            }
            return true;
        }

        internal static void Exported(UIApplication app, string path)
        {
            if (!NeedsExport(app.ActiveUIDocument?.Document)) return;
            _workbookPath = path;
            _stage = 2;
            DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.WellDoneYourTurnInExcel"),
                UiLanguage.Get("DemoTourService.TheWorkbookIsHere") + path +
                UiLanguage.Get("DemoTourService.InTheNomenclatureSheetChangeOneOrMore") + _fieldName +
                UiLanguage.Get("DemoTourService.ForExampleXL003XL103The") + _referenceFieldName +
                UiLanguage.Get("DemoTourService.ColumnIsAFixedReferenceLeaveItUnchanged"));
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "excel");
        }

        internal static bool ValidateWorkbook(UIApplication app, string path,
            List<Dictionary<string, string>> rows)
        {
            Document document = app.ActiveUIDocument?.Document;
            if (!IsActive(document)) return true;
            if (_stage != 2 || !string.Equals(path, _workbookPath, StringComparison.OrdinalIgnoreCase))
                return RejectWorkbook(app, UiLanguage.Get("DemoTourService.BulbasaurIsWaitingForTheExport"),
                    UiLanguage.Get("DemoTourService.FirstExportTheWalkthroughScheduleThenEditAnd"));

            // Contrôler les quatre identifiants ET le repère avant d'ouvrir une transaction.
            // Une ligne supplémentaire ou dupliquée ne doit jamais pouvoir modifier une autre place.
            if (rows == null || _references == null || _references.Count != 4 || rows.Count != 4)
                return RejectWorkbook(app, UiLanguage.Get("DemoTourService.BulbasaurIsCheckingTheSpaces"),
                    UiLanguage.Get("DemoTourService.TheWorkbookMustContainExactlyTheFourExported"));

            var numbers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row == null || !row.TryGetValue("UniqueId", out string uid) ||
                    !_references.TryGetValue(uid ?? "", out string reference) || numbers.ContainsKey(uid))
                    return RejectWorkbook(app, UiLanguage.Get("DemoTourService.BulbasaurIsCheckingTheSpaces"),
                        UiLanguage.Get("DemoTourService.ASpaceIDIsMissingUnknownOrDuplicated"));
                if (!row.TryGetValue(_referenceFieldName, out string workbookReference) ||
                    !string.Equals(workbookReference, reference, StringComparison.Ordinal))
                    return RejectWorkbook(app, UiLanguage.Get("DemoTourService.BulbasaurIsProtectingTheMark"),
                        UiLanguage.Get("DemoTourService.The") + _referenceFieldName + UiLanguage.Get("DemoTourService.ColumnIsAFixedReferenceRestoreItsOriginal"));
                if (!row.TryGetValue(_fieldName, out string number))
                    return RejectWorkbook(app, UiLanguage.Get("DemoTourService.BulbasaurIsLookingForTheNumbers"),
                        UiLanguage.Get("DemoTourService.The") + _fieldName + UiLanguage.Get("DemoTourService.ColumnIsMissingStartAgainFromTheWorkbook"));
                numbers.Add(uid, number ?? "");
            }

            bool changed = numbers.Any(pair =>
                !string.Equals(pair.Value, _initialNumbers[pair.Key], StringComparison.Ordinal));
            if (!changed)
                return RejectWorkbook(app, UiLanguage.Get("DemoTourService.BulbasaurIsWaitingForAChange"),
                    UiLanguage.Get("DemoTourService.ChangeAtLeastOneNumberInNomenclatureFor") + _referenceFieldName + UiLanguage.Get("DemoTourService.StaysFixed"));

            _expectedNumbers = numbers;
            var changes = new List<string>();
            foreach (var pair in _references.OrderBy(pair => pair.Value))
            {
                string uid = pair.Key;
                string label = pair.Value.Substring((DemoProjectBuilder.DemoPrefix + "EXCEL_").Length);
                if (_initialNumbers[uid] != numbers[uid])
                    changes.Add(UiLanguage.Get("DemoTourService.Space") + label + " · " + _fieldName + " : " +
                        DisplayExcelValue(_initialNumbers[uid]) + " → " + DisplayExcelValue(numbers[uid]));
            }
            _verifiedChanges = string.Join("\n", changes);
            return true;
        }

        private static string DisplayExcelValue(string value) => string.IsNullOrEmpty(value) ? UiLanguage.Get("DemoTourService.Empty") : value;

        private static bool RejectWorkbook(UIApplication app, string title, string text)
        {
            DemoTourMessage.Show(app.MainWindowHandle, title, text);
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "excel");
            return false;
        }

        // L'import guidé accepte chaque numéro modifié, jamais le repère fixe.
        internal static Parameter ImportParameter(FamilyInstance place, string header)
        {
            if (place == null || !NeedsImport(place.Document)) return null;
            if (string.Equals(header, _fieldName, StringComparison.OrdinalIgnoreCase))
                return DemoProjectBuilder.ExcelValueParameter(place, _fieldName);
            return null;
        }

        internal static void Imported(UIApplication app)
        {
            UIDocument uiDocument = app.ActiveUIDocument;
            if (!NeedsImport(uiDocument?.Document)) return;
            var places = DemoProjectBuilder.FindExcelParking(uiDocument.Document);
            bool valid = places.Count == 4 && _expectedNumbers != null && places.All(place =>
                _expectedNumbers.TryGetValue(place.UniqueId, out string expectedNumber) &&
                string.Equals(DemoProjectBuilder.ExcelValueParameter(place, _fieldName)?.AsString() ?? "",
                    expectedNumber, StringComparison.Ordinal) &&
                _references.TryGetValue(place.UniqueId, out string reference) &&
                string.Equals(place.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                    reference, StringComparison.Ordinal));
            if (!valid)
            {
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurIsCheckingTheImport"),
                    UiLanguage.Get("DemoTourService.TheImportFinishedButRevitDoesNotMatch"));
                Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "excel");
                return;
            }
            try
            {
                using (var tx = new Transaction(uiDocument.Document,
                    "BIMaestro - Afficher les numéros importés sur les parkings"))
                {
                    tx.Start();
                    DemoProjectBuilder.SynchronizeExcelParkingNumbers(places, _fieldName);
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurIsCheckingTheDisplay"),
                    UiLanguage.Get("DemoTourService.TheWorkbookValuesAreInRevitButThe") +
                    ex.Message);
                return;
            }
            if (!places.All(place => _expectedNumbers.TryGetValue(place.UniqueId, out string expectedNumber) &&
                string.Equals(place.LookupParameter("CML_Numéros de place")?.AsString() ?? "",
                    expectedNumber, StringComparison.Ordinal) &&
                string.Equals(place.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "",
                    expectedNumber, StringComparison.Ordinal)))
            {
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurIsCheckingTheDisplay"),
                    UiLanguage.Get("DemoTourService.TheImportedNumberDoesNotMatchTheText"));
                return;
            }
            _stage = 3;
            View targetView = _parkingViewId == null ? null : uiDocument.Document.GetElement(_parkingViewId) as View;
            if (targetView != null && uiDocument.ActiveView.Id != targetView.Id)
            {
                _pendingAction = 2;
                _deadlineUtc = DateTime.UtcNow.AddSeconds(30);
                uiDocument.RequestViewChange(targetView);
            }
            else
            {
                _pendingAction = 2;
                ProcessIdling(app);
            }
        }
    }

    internal static class DemoViewTemplateGuide
    {
        private static string _documentPath;
        private static string _exportedFile;
        private static bool _waitingForImport;

        internal static string ExportedFileFor(Document document) =>
            IsCurrent(document) && _waitingForImport ? _exportedFile : null;

        internal static bool IsGuided(Document document) => IsCurrent(document);
        internal static bool IsImportStage(Document document) => IsCurrent(document) && _waitingForImport;

        internal static void Begin(Document document)
        {
            PrepareVisualComparison(document);
            _documentPath = document?.PathName;
            _exportedFile = null;
            _waitingForImport = false;
        }

        private static void PrepareVisualComparison(Document document)
        {
            var views = new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(view => view.Name == "BIMaestro - 06 Gabarit source" || view.Name == "BIMaestro - 07 Gabarit cible").ToList();
            if (views.Count != 2) throw new InvalidOperationException(UiLanguage.Get("DemoTourService.TheTutorialSSourceAndTargetViewsCannot"));
            using (var tx = new Transaction(document, "BIMaestro - Comparaison des gabarits"))
            {
                tx.Start();
                const string patternName = "BIMaestro - Formation tirets";
                var pattern = new FilteredElementCollector(document).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                    .FirstOrDefault(item => item.Name == patternName);
                if (pattern == null)
                {
                    var definition = new LinePattern(patternName);
                    definition.SetSegments(new List<LinePatternSegment> {
                        new LinePatternSegment(LinePatternSegmentType.Dash, UnitUtils.ConvertToInternalUnits(4, UnitTypeId.Millimeters)),
                        new LinePatternSegment(LinePatternSegmentType.Space, UnitUtils.ConvertToInternalUnits(2, UnitTypeId.Millimeters)) });
                    pattern = LinePatternElement.Create(document, definition);
                }
                var solidFill = new FilteredElementCollector(document).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .FirstOrDefault(item => item.GetFillPattern().IsSolidFill && item.GetFillPattern().Target == FillPatternTarget.Drafting);
                foreach (View3D view in views)
                {
                    bool source = view.Name == "BIMaestro - 06 Gabarit source";
                    view.ViewTemplateId = ElementId.InvalidElementId;
                    view.DetailLevel = ViewDetailLevel.Fine;
                    view.DisplayStyle = DisplayStyle.FlatColors;
                    var color = source ? new Autodesk.Revit.DB.Color(220, 35, 45) : new Autodesk.Revit.DB.Color(25, 95, 220);
                    var surface = source ? new Autodesk.Revit.DB.Color(255, 190, 190) : new Autodesk.Revit.DB.Color(175, 215, 255);
                    var graphics = new OverrideGraphicSettings();
                    graphics.SetProjectionLineColor(color);
                    graphics.SetCutLineColor(color);
                    graphics.SetProjectionLineWeight(source ? 5 : 2);
                    graphics.SetCutLineWeight(source ? 5 : 2);
                    graphics.SetProjectionLinePatternId(source ? pattern.Id : LinePatternElement.GetSolidPatternId());
                    if (solidFill != null)
                    {
                        graphics.SetSurfaceForegroundPatternId(solidFill.Id);
                        graphics.SetSurfaceForegroundPatternColor(surface);
                        graphics.SetSurfaceForegroundPatternVisible(true);
                    }
                    foreach (BuiltInCategory category in new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
                        BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_Parking })
                    {
                        var id = new ElementId(category);
                        if (view.IsCategoryOverridable(id)) view.SetCategoryOverrides(id, graphics);
                    }
                }
                tx.Commit();
            }
        }

        internal static void OnCommandOpened(Document document)
        {
            if (IsCurrent(document)) Couleur.AppearanceOnboarding.ConsumeTourClick("view-template");
        }

        internal static void OnExportCompleted(UIApplication app, string path)
        {
            UIDocument uiDocument = app?.ActiveUIDocument;
            if (!IsCurrent(uiDocument?.Document) || _waitingForImport) return;
            View3D target = new FilteredElementCollector(uiDocument.Document).OfClass(typeof(View3D))
                .Cast<View3D>().FirstOrDefault(view => view.Name == "BIMaestro - 07 Gabarit cible");
            if (target == null) return;
            uiDocument.ActiveView = target;
            _exportedFile = path;
            _waitingForImport = true;
            DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.WellDoneExportSucceeded"),
                UiLanguage.Get("DemoTourService.TheTargetViewIsNowBlueWithThin") +
                path + UiLanguage.Get("DemoTourService.ThenChooseCreateUpdateARealNamedTemplate"));
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "view-template");
        }

        internal static void OnImportCompleted(Document document, IntPtr owner)
        {
            if (!IsCurrent(document) || !_waitingForImport) return;
            var target = new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(view => view.Name == "BIMaestro - 07 Gabarit cible");
            View template = target == null ? null : document.GetElement(target.ViewTemplateId) as View;
            var settings = template?.GetCategoryOverrides(new ElementId(BuiltInCategory.OST_PipeCurves));
            var color = settings?.ProjectionLineColor;
            if (template == null || color == null || !color.IsValid || color.Red != 220 || color.Green != 35 || color.Blue != 45 ||
                settings.ProjectionLineWeight != 5 || settings.ProjectionLinePatternId == LinePatternElement.GetSolidPatternId())
            {
                DemoTourMessage.Show(owner, UiLanguage.Get("DemoTourService.CheckTheImportedTemplate"), UiLanguage.Get("DemoTourService.TheTargetMustReceiveARealTemplateWith"));
                return;
            }
            using (var tx = new Transaction(document, "BIMaestro - Afficher le gabarit importé"))
            {
                tx.Start();
                foreach (BuiltInCategory category in new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
                    BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_Parking })
                {
                    var id = new ElementId(category);
                    if (target.IsCategoryOverridable(id)) target.SetCategoryOverrides(id, new OverrideGraphicSettings());
                }
                tx.Commit();
            }
            new UIDocument(document).RefreshActiveView();
            _documentPath = null;
            _exportedFile = null;
            _waitingForImport = false;
            DemoTourCompletion.Show(owner, "view-template",
                UiLanguage.Get("DemoTourService.TheTargetShouldNowShowTheSourceS"));
        }

        private static bool IsCurrent(Document document) => document != null &&
            !string.IsNullOrWhiteSpace(_documentPath) &&
            string.Equals(document.PathName, _documentPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static class DemoTrainingInvitation
    {
        private static string _pendingPath;
        private static DateTime _readyAfterUtc;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        internal static void OnDocumentOpened(Document document)
        {
            string path = document?.PathName;
            if (!DemoProjectBuilder.IsLearningDocument(document))
                return;
            _pendingPath = path;
            _readyAfterUtc = DateTime.UtcNow.AddMilliseconds(600);
        }

        // Return true while this invitation owns the active learning model, so other
        // first-run prompts wait until the learner has made a choice.
        internal static bool ProcessIdling(UIApplication uiApp)
        {
            if (DemoTourPendingSelection.ProcessIdling(uiApp)) return true;
            if (DemoTourCompletion.ProcessIdling(uiApp)) return true;
            if (DemoExcelExercise.ProcessIdling(uiApp)) return true;
            if (string.IsNullOrEmpty(_pendingPath) || uiApp?.ActiveUIDocument?.Document == null)
                return false;
            string activePath = uiApp.ActiveUIDocument.Document.PathName;
            if (!string.Equals(activePath, _pendingPath, StringComparison.OrdinalIgnoreCase))
                return false;
            if (DateTime.UtcNow < _readyAfterUtc || GetForegroundWindow() != uiApp.MainWindowHandle ||
                BIMaestro.Welcome.WelcomeManager.HasVisibleBIMaestroWindow())
                return true;

            _pendingPath = null;
            var invitation = new DemoTrainingChoiceWindow();
            new WindowInteropHelper(invitation).Owner = uiApp.MainWindowHandle;
            invitation.ShowDialog();
            if (string.IsNullOrEmpty(invitation.SelectedTour)) return true;
            try
            {
                string message = null;
                DemoToursCommand.RunChoice(uiApp, invitation.SelectedTour, ref message);
            }
            catch (Exception ex)
            {
                DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.Get("DemoTourService.BulbasaurNeedsHelp"),
                    UiLanguage.Get("DemoTourService.TheTutorialCouldNotStart") + ex.Message);
            }
            return true;
        }
    }

    internal sealed class DemoTrainingChoiceWindow : DemoDiscoveryWindow
    {
        internal DemoTrainingChoiceWindow() { Title = UiLanguage.Get("DemoTourService.BIMaestroChooseATutorial"); }
    }

    internal static class DemoTourService
    {
        private static readonly Dictionary<Window, DemoWindowGuide> ActiveGuides = new Dictionary<Window, DemoWindowGuide>();
        private static FamilyRibbonTutorialGuide _familyRibbonGuide;
        internal static bool TutorialFavoritesSelected { get; private set; }
        private static Famille.Collection _familyTutorialFavorites;
        private static List<string> _familyTutorialRecents;
        private static Autodesk.Revit.ApplicationServices.Application _placementApplication;
        private static EventHandler<Autodesk.Revit.DB.Events.DocumentChangedEventArgs> _placementWatcher;

        internal static List<string> GetTutorialRecentFamiliesForRosace()
            => _familyRibbonGuide == null || _familyTutorialRecents == null
                ? null : new List<string>(_familyTutorialRecents);

        internal static bool IsTutorialFamilyForRosace(string path)
            => _familyRibbonGuide != null && !string.IsNullOrWhiteSpace(path) &&
                ((_familyTutorialRecents?.Contains(path, StringComparer.OrdinalIgnoreCase) ?? false) ||
                 (_familyTutorialFavorites?.Paths.Contains(path, StringComparer.OrdinalIgnoreCase) ?? false));

        private static void StopTutorialPlacementWatch()
        {
            if (_placementApplication != null && _placementWatcher != null)
                _placementApplication.DocumentChanged -= _placementWatcher;
            _placementWatcher = null;
            _placementApplication = null;
        }

        internal static void WatchTutorialFamilyPlacement(Document document, FamilySymbol symbol)
        {
            StopTutorialPlacementWatch();
            if (_familyRibbonGuide == null || document == null || symbol == null) return;
            ElementId symbolId = symbol.Id;
            _placementApplication = document.Application;
            _placementWatcher = (sender, args) =>
            {
                try
                {
                    Document changed = args.GetDocument();
                    if (!ReferenceEquals(changed, document) && !changed.Equals(document)) return;
                    bool placed = args.GetAddedElementIds().Any(id =>
                        changed.GetElement(id) is FamilyInstance instance &&
                        instance.Symbol.Id.Equals(symbolId));
                    if (!placed) return;
                    StopTutorialPlacementWatch();
                    ReportExternalAction("radial-tutorial-chaise-placed");
                }
                catch (Autodesk.Revit.Exceptions.InvalidObjectException)
                {
                    StopTutorialPlacementWatch();
                }
            };
            _placementApplication.DocumentChanged += _placementWatcher;
        }

        internal static bool AttachIfRequested(string id, Window window)
        {
            if (!Couleur.AppearanceOnboarding.ConsumeTourClick(id)) return false;
            return StartInWindowCore(id, window, preparedExercise: true);
        }

        // A TUTO button can restart its guide in an already open dialog. The
        // ribbon onboarding uses the same path after its command has opened it.
        internal static bool StartInWindow(string id, Window window)
            => StartInWindowCore(id, window, preparedExercise: false);

        private static bool StartInWindowCore(string id, Window window, bool preparedExercise)
        {
            if (window?.Content is not Grid) return false;
            if (!DemoTourCatalog.Steps.TryGetValue(id, out DemoStep[] steps)) return false;
            if (id == "family-browser") _familyRibbonGuide?.Close();
            if (ActiveGuides.TryGetValue(window, out DemoWindowGuide previous))
            {
                if (previous.TourId == id && previous.IsPreparedExercise)
                    preparedExercise = true;
                TutorialAnalytics.End(previous.TourId, "interrupted");
                previous.Close();
            }
            if (id == "family-browser" && window is Famille.FamilyBrowserWindow browser &&
                !browser.BeginTutorialCatalog()) return false;
            if (!preparedExercise && DemoTourCatalog.StandaloneSteps.TryGetValue(id, out DemoStep[] standalone))
                steps = standalone;
            if (id == "organizer" && preparedExercise && DemoOrganizerExercise.IsRotatedPass)
                steps = DemoTourCatalog.OrganizerRotatedSteps;
            if (id == "organizer" && preparedExercise && DemoOrganizerExercise.IsSheetPass)
                steps = new[] {
                    new DemoStep(UiLanguage.Get("DemoTourService.TheSelectedSheets"), UiLanguage.Get("DemoTourService.TheThreeTestSheetsABAndC"), "OrganizerHeaderTitle", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.SheetNumber"), UiLanguage.Get("DemoTourService.ChooseSheetNumberKeepThePrefixAndSuffix"), "ParameterComboBox", true),
                    new DemoStep(UiLanguage.Get("DemoTourService.RenumberTheSheets"), UiLanguage.Get("DemoTourService.ClickRenameSheets34And5Become"), "DemoRenameButton")
                };
            if (id == "pipe-calculation")
            {
                window.Width = Math.Max(window.Width, 760);
                window.Height = Math.Max(window.Height, 520);
            }
            if (id == "clash-3d" && window is Analyse.SmartCheckWindow clash)
                clash.ConfigureTutorial(preparedExercise);
            ActiveGuides[window] = new DemoWindowGuide(window, steps, id, preparedExercise);
            return true;
        }

        internal static void ObserveHistoryPreview(Window historyWindow, IntPtr owner)
        {
            if (!ActiveGuides.TryGetValue(historyWindow, out DemoWindowGuide guide)) return;
            bool detailed = guide.WaitingFor("history-preview-detailed-observed");
            if (!detailed && !guide.WaitingFor("history-preview-simple-observed")) return;
            string observedAction = detailed ? "history-preview-detailed-observed" : "history-preview-simple-observed";
            historyWindow.Hide();
            var card = new Window { Title = UiLanguage.Get("DemoTourService.BIMaestroLookAtThePreview"), Width = 390,
                SizeToContent = SizeToContent.Height, ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize, Background = Brushes.White,
                Left = SystemParameters.WorkArea.Right - 420, Top = SystemParameters.WorkArea.Bottom - 260 };
            new WindowInteropHelper(card).Owner = owner;
            var panel = new StackPanel { Margin = new Thickness(18) };
            card.Content = new Border { BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(2), Child = panel };
            panel.Children.Add(new TextBlock { Text = detailed ? UiLanguage.Get("DemoTourService.DetailedPreview") : UiLanguage.Get("DemoTourService.SimplePreview"), FontSize = 18, Foreground = DemoTourPalette.Accent });
            panel.Children.Add(new TextBlock { Text = detailed
                ? UiLanguage.Get("DemoTourService.CompareTheRedTablesAndChairsWithThe")
                : UiLanguage.Get("DemoTourService.LookAtTheRedVolumesTheyShowThe"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
            var seen = new Button { Content = UiLanguage.Get("DemoTourService.IHaveSeenThePreviewContinue"), Padding = new Thickness(10),
                Background = DemoTourPalette.Accent, Foreground = Brushes.White };
            panel.Children.Add(seen);
            bool confirmed = false;
            seen.Click += (_, __) => { confirmed = true; card.Close(); };
            card.Closed += (_, __) => {
                if (!ActiveGuides.ContainsKey(historyWindow)) return;
                historyWindow.Show(); historyWindow.Activate();
                if (confirmed)
                {
                    if (detailed && historyWindow is Analyse.ElementHistoryWindow) Analyse.ElementHistoryTracker.CaptureDetailedDeletedMesh = false;
                    ReportAction(historyWindow, observedAction);
                }
            };
            historyWindow.Closed += (_, __) => card.Close();
            card.Show();
        }

        internal static Window ObserveHistoryResult(Window historyWindow, IntPtr owner, string heading, string text, Action confirmed)
        {
            if (!IsActive(historyWindow)) return null;
            historyWindow.Hide();
            var card = new Window { Title = UiLanguage.Get("DemoTourService.BIMaestroLookAtTheResult"), Width = 390,
                SizeToContent = SizeToContent.Height, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
                Background = Brushes.White, Left = SystemParameters.WorkArea.Right - 420, Top = SystemParameters.WorkArea.Bottom - 250 };
            new WindowInteropHelper(card).Owner = owner;
            var body = new StackPanel { Margin = new Thickness(18) };
            card.Content = new Border { BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(2), Child = body };
            body.Children.Add(new TextBlock { Text = heading, FontSize = 18, Foreground = DemoTourPalette.Accent, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
            var seen = new Button { Content = UiLanguage.Get("DemoTourService.IHaveSeenTheResultContinue"), Padding = new Thickness(10), Background = DemoTourPalette.Accent, Foreground = Brushes.White };
            body.Children.Add(seen);
            bool accepted = false;
            seen.Click += (_, __) => { accepted = true; card.Close(); };
            card.Closed += (_, __) => {
                if (!IsActive(historyWindow)) return;
                historyWindow.Show(); historyWindow.Activate();
                if (accepted) confirmed();
            };
            historyWindow.Closed += (_, __) => card.Close();
            card.Show();
            return card;
        }

        internal static void ReportAction(Window window, string action)
        {
            if (ActiveGuides.TryGetValue(window, out DemoWindowGuide guide))
                guide.CompleteAction(action);
        }

        // Revit ribbon commands are outside the browser WPF tree. Keep the
        // browser guide alive while the learner assigns and tries a shortcut.
        internal static void ReportExternalAction(string action)
        {
            if (action == "radial-tutorial-favorites-selected") TutorialFavoritesSelected = true;
            _familyRibbonGuide?.CompleteAction(action);
            foreach (var guide in ActiveGuides.Values.Where(g => g.TourId == "family-browser").ToArray())
                guide.CompleteAction(action);
        }

        internal static Famille.Collection GetTutorialFavoritesForRosace()
        {
            if (_familyRibbonGuide == null || _familyTutorialFavorites == null) return null;
            return new Famille.Collection
            {
                Id = _familyTutorialFavorites.Id,
                Name = _familyTutorialFavorites.Name,
                Paths = new List<string>(_familyTutorialFavorites.Paths)
            };
        }

        internal static bool IsTutorialChaiseForRosace(string familyPath)
        {
            if (_familyRibbonGuide == null || _familyTutorialFavorites?.Paths == null ||
                string.IsNullOrWhiteSpace(familyPath)) return false;
            string catalogPart = System.IO.Path.DirectorySeparatorChar + "NavigateurFamilles" +
                System.IO.Path.DirectorySeparatorChar + "Familles" +
                System.IO.Path.DirectorySeparatorChar;
            return familyPath.IndexOf(catalogPart, StringComparison.OrdinalIgnoreCase) >= 0 &&
                System.IO.Path.GetFileNameWithoutExtension(familyPath)
                    .IndexOf("chaise", StringComparison.OrdinalIgnoreCase) >= 0 &&
                _familyTutorialFavorites.Paths.Any(path =>
                    string.Equals(path, familyPath, StringComparison.OrdinalIgnoreCase));
        }

        private static void ContinueFamilyGuideInRevit(DemoStep[] steps, Famille.FamilyBrowserWindow browser)
        {
            IntPtr owner = Famille.FamilyBrowserCommand.uiapp?.MainWindowHandle ?? IntPtr.Zero;
            if (owner == IntPtr.Zero) return;
            _familyRibbonGuide?.Close();
            TutorialFavoritesSelected = false;
            _familyTutorialFavorites = browser.TryGetTutorialFavoritesForRosace();
            _familyTutorialRecents = browser.GetTutorialRecentFamilyPaths();
            int ribbonStartIndex = Array.FindIndex(steps, step => step.Target == "RevitFamilySplit");
            if (ribbonStartIndex < 0) return;
            var guide = new FamilyRibbonTutorialGuide(owner, steps, ribbonStartIndex, () =>
            {
                StopTutorialPlacementWatch();
                _familyTutorialFavorites = null;
                _familyTutorialRecents = null;
                _familyRibbonGuide = null;
            });
            _familyRibbonGuide = guide;
            // The browser keeps the exercise favourites only in memory. Snapshot
            // them first, then restore the learner's paths before closing it.
            browser.EndTutorialCatalog();
            browser.Close();
            guide.Show();
        }

        internal static void ReportRestoration(Window window, bool furnitureRestored)
        {
            if (ActiveGuides.TryGetValue(window, out DemoWindowGuide guide))
                guide.CompleteRestoration(furnitureRestored);
        }

        internal static void ReportParameterRestoration(Window window, bool exerciseRestored)
        {
            if (ActiveGuides.TryGetValue(window, out DemoWindowGuide guide))
                guide.CompleteParameterRestoration(exerciseRestored);
        }

        internal static bool IsActive(Window window) => ActiveGuides.ContainsKey(window);

        private sealed class DemoWindowGuide
        {
            internal readonly string TourId;
            internal readonly bool IsPreparedExercise;
            private readonly Window _window;
            private readonly DemoStep[] _steps;
            private readonly Grid _root;
            private readonly Border _card;
            private readonly TextBlock _title, _text;
            private readonly Button _previous, _next;
            private AdornerLayer _layer;
            private TargetAdorner _adorner;
            private Action _detachAction;
            private int _index;
            private bool _completed;
            private bool _windowClosed, _closed;

            internal DemoWindowGuide(Window window, DemoStep[] steps, string tourId, bool preparedExercise)
            {
                TourId = tourId;
                IsPreparedExercise = preparedExercise;
                _window = window; _steps = steps;
                _root = window.Content as Grid;
                if (_root == null) return;
                _card = new Border
                {
                    Width = 320, Padding = new Thickness(14), Margin = new Thickness(12),
                    CornerRadius = new CornerRadius(12), Background = Brushes.White,
                    BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                Grid.SetRowSpan(_card, Math.Max(1, _root.RowDefinitions.Count));
                Grid.SetColumnSpan(_card, Math.Max(1, _root.ColumnDefinitions.Count));
                Panel.SetZIndex(_card, 1000);
                if (tourId == "clash-3d") Grid.SetColumn(_card, 1);
                var stack = new StackPanel(); _card.Child = stack;
                var heading = new StackPanel { Orientation = Orientation.Horizontal };
                heading.Children.Add(new Image { Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                    Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0) });
                _title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 15,
                    Width = 245, TextWrapping = TextWrapping.Wrap };
                heading.Children.Add(_title); stack.Children.Add(heading);
                _text = new TextBlock { Margin = new Thickness(0, 9, 0, 12), TextWrapping = TextWrapping.Wrap };
                var textScroll = new ScrollViewer { Content = _text,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 280 };
                stack.Children.Add(textScroll);
                window.SizeChanged += (_, __) => textScroll.MaxHeight = Math.Max(80, Math.Min(280, window.ActualHeight - 200));
                var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right };
                stack.Children.Add(buttons);
                _previous = AddButton(buttons, UiLanguage.Get("DemoTourService.Previous"), () => { _index--; ShowStep(); });
                _next = AddButton(buttons, UiLanguage.Get("DemoTourService.Next"), () =>
                {
                    if (!_completed) return;
                    if (_index + 1 == _steps.Length)
                    {
                        TutorialAnalytics.Complete(TourId);
                        Close();
                        DemoTourCompletion.Show(new WindowInteropHelper(_window).Owner, TourId,
                            UiLanguage.Get("DemoTourService.YouHaveFinishedThisTutorial"), () => _window.Close());
                        return;
                    }
                    _index++; ShowStep();
                });
                AddButton(buttons, UiLanguage.Get("DemoTourService.Quit"), () => { TutorialAnalytics.End(TourId, "quit"); Close(); });
                _root.Children.Add(_card);
                if (window.IsLoaded)
                    window.Dispatcher.BeginInvoke(new Action(ShowStep), DispatcherPriority.Loaded);
                else
                    window.Loaded += OnLoaded;
                window.Closed += (_, __) => { _windowClosed = true; Close(); ActiveGuides.Remove(window); };
            }

            private static Button AddButton(Panel panel, string text, Action action)
            {
                var button = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(4, 0, 0, 0) };
                button.Click += (_, __) => action(); panel.Children.Add(button);
                return button;
            }

            private void OnLoaded(object sender, RoutedEventArgs args)
            {
                _favoriteHighlightTimer?.Stop(); _favoriteHighlightTimer = null;
                _window.Loaded -= OnLoaded;
                ShowStep();
            }

            private void ShowStep()
            {
                if (_closed) return;
                _favoriteHighlightTimer?.Stop(); _favoriteHighlightTimer = null;
                _detachAction?.Invoke();
                _detachAction = null;
                RemoveHighlight();
                if (_index < 0 || _index >= _steps.Length) return;
                TutorialAnalytics.Step(TourId, _index, _steps[_index].Title);
                if (TourId == "family-browser" && _steps[_index].Target == "RevitFamilySplit" &&
                    _window is Famille.FamilyBrowserWindow familyBrowser)
                {
                    ContinueFamilyGuideInRevit(_steps, familyBrowser);
                    return;
                }
                _completed = false;
                DemoStep step = _steps[_index];
                if (_window is Analyse.SmartCheckWindow clash) clash.PrepareTutorialStep(step.Target, step.CompletionEvent);
                if (_window is Famille.FamilyBrowserWindow browser)
                    browser.PrepareTutorialStep(step.Target, step.CompletionEvent);
                if (_window.FindName("ReservationFamiliesTab") is TabItem familiesTab &&
                    (step.Target == "ReservationFamiliesTab" || step.Target == "cbConfigSupport" ||
                     step.Target == "cbLoadedType" || step.Target == "btnBrowseRfa" ||
                     step.Target == "panelMapRectWall" || step.Target == "cbVerticalReference" ||
                     step.Target == "btnApplyMapping"))
                    familiesTab.IsSelected = true;
                else if (_window.FindName("ReservationSettingsTab") is TabItem settingsTab &&
                    (step.Target == "chkDefaultNorme" || step.Target == "chkDefaultDynamo"))
                    settingsTab.IsSelected = true;
                else if (_window.FindName("ReservationExecutionTab") is TabItem executionTab)
                    executionTab.IsSelected = true;
                // The reservation launch button is in the lower-right footer.
                // Keep the guide opposite it so the learner can actually click it.
                _card.HorizontalAlignment = step.Target == "DemoRunReservationButton" ||
                    step.Target == "cbVerticalReference" || step.Target == "btnApplyMapping" ||
                    (step.Target == "OkButton" && TourId != "pipe-calculation") ||
                    step.Target == "DemoRenameButton" ||
                    step.Target == "SearchMetadataAssistantButton" || step.Target == "FamilyListView" || step.Target == "GroupedFamilyListView"
                    ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                _card.VerticalAlignment = step.Target == "SearchMetadataAssistantButton" ? VerticalAlignment.Top : TourId == "pipe-calculation"
                    ? VerticalAlignment.Center
                    : TourId == "organizer" && step.Target == "SortByLevelCheckBox"
                    ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                _title.Text = $"{_index + 1}/{_steps.Length} · {step.Title}";
                if (TourId == "clash-3d")
                {
                    _card.HorizontalAlignment = HorizontalAlignment.Center;
                    _card.VerticalAlignment = VerticalAlignment.Top;
                }
                _text.Text = step.Text;
                _previous.IsEnabled = _index > 0;
                _next.Content = _index + 1 == _steps.Length ? UiLanguage.Get("DemoTourService.Finish") : UiLanguage.Get("DemoTourService.Next");
                _completed = step.IsExplanation;
                _next.IsEnabled = step.IsExplanation;
                if (_window is Analyse.SmartCheckWindow completedClash && completedClash.HasCompletedTutorialAction(step.CompletionEvent))
                { _completed = true; _next.IsEnabled = true; }
                if (step.CompletionEvent == "load-bureau-commun" &&
                    Famille.FamilyBrowserCommand.uiapp?.ActiveUIDocument == null)
                {
                    _text.Text += UiLanguage.Get("DemoTourService.NoProjectIsOpenOpenOneInRevit");
                    _completed = true;
                    _next.IsEnabled = true;
                }
                if (step.CompletionEvent == "favorite-added" &&
                    _window is Famille.FamilyBrowserWindow favoriteBrowser &&
                    favoriteBrowser.HasTutorialChaiseFavorite())
                {
                    _text.Text += UiLanguage.Get("DemoTourService.AChairIsAlreadyInFavoritesOpenThe");
                    _completed = true;
                    _next.IsEnabled = true;
                }
                if (step.CompletionEvent == "radial-tutorial-chaise-placed" &&
                    Famille.FamilyBrowserCommand.uiapp?.ActiveUIDocument == null)
                {
                    _text.Text += UiLanguage.Get("DemoTourService.NoRevitProjectIsOpenOpenAModel");
                    _completed = true;
                    _next.IsEnabled = true;
                }
                int current = _index;
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed || _index != current) return;
                    Highlight(step.Target, current);
                    if (!step.IsExplanation && step.CompletionEvent == null)
                        WatchAction(step.Target, current);
                }), DispatcherPriority.Loaded);
            }

            internal bool WaitingFor(string action) => _index >= 0 && _index < _steps.Length &&
                _steps[_index].CompletionEvent == action && !_completed;

            internal void CompleteAction(string action)
            {
                if (_index >= 0 && _index < _steps.Length &&
                    _steps[_index].CompletionEvent == "radial-tutorial-chaise-placed" &&
                    action == "radial-opened")
                {
                    _text.Text = UiLanguage.Get("DemoTourService.TheRadialMenuIsOpenRightClickThe");
                    return;
                }
                if (_index >= 0 && _index < _steps.Length &&
                    string.Equals(_steps[_index].CompletionEvent, action, StringComparison.Ordinal))
                {
                    if (_steps[_index].IsExplanation && _completed)
                    {
                        if (_index + 1 < _steps.Length) { _index++; ShowStep(); }
                    }
                    else
                        CompleteStep(_index);
                }
            }

            private void WatchAction(string targetName, int expectedIndex)
            {
                if (_index != expectedIndex || !(_window.FindName(targetName) is FrameworkElement target)) return;
                if (targetName == "RestoreDeletedButton" || targetName == "RestoreParametersButton")
                    return; // Wait for a committed and verified restoration.
                if (target is RadioButton radio && (targetName == "SimpleMeshModeRadio" || targetName == "DetailedMeshModeRadio"))
                {
                    _completed = radio.IsChecked == true;
                    _next.IsEnabled = _completed;
                    RoutedEventHandler handler = (_, __) => CompleteStep(expectedIndex);
                    radio.Checked += handler;
                    _detachAction = () => radio.Checked -= handler;
                }
                else if (target is System.Windows.Controls.TextBox textBox)
                {
                    if (targetName == "PrefixTextBox" &&
                        string.Equals(textBox.Text, "PK-", StringComparison.Ordinal))
                    {
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                    TextChangedEventHandler handler = (_, __) =>
                    {
                        if (targetName != "PrefixTextBox" ||
                            string.Equals(textBox.Text, "PK-", StringComparison.Ordinal))
                            CompleteStep(expectedIndex);
                    };
                    textBox.TextChanged += handler;
                    _detachAction = () => textBox.TextChanged -= handler;
                }
                else if (target is CheckBox checkBox &&
                    (targetName == "IncludeDuctsCheckBox" || targetName == "ExportToExcelCheckBox" ||
                     targetName == "SortByLevelCheckBox"))
                {
                    // A checked option already satisfies this step. Clicking it again
                    // must not force the learner to undo and redo the same choice.
                    _completed = checkBox.IsChecked == true;
                    _next.IsEnabled = _completed;
                    RoutedEventHandler checkedHandler = (_, __) => CompleteStep(expectedIndex);
                    RoutedEventHandler uncheckedHandler = (_, __) =>
                    {
                        _completed = false;
                        _next.IsEnabled = false;
                    };
                    checkBox.Checked += checkedHandler;
                    checkBox.Unchecked += uncheckedHandler;
                    _detachAction = () =>
                    {
                        checkBox.Checked -= checkedHandler;
                        checkBox.Unchecked -= uncheckedHandler;
                    };
                }
                else if (target is ButtonBase button)
                {
                    RoutedEventHandler handler = (_, __) => CompleteStep(expectedIndex);
                    button.Click += handler;
                    _detachAction = () => button.Click -= handler;
                }
                else if (target is Selector selector)
                {
                    if (TourId == "history" && targetName == "VisualCardsList" && selector.SelectedItem != null)
                    {
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                    SelectionChangedEventHandler handler = (_, __) =>
                    {
                        string selected = selector.SelectedItem?.ToString() ?? "";
                        if ((targetName != "ActionFilterCombo" ||
                             IsDeletionAction(selected)) &&
                            (targetName != "ParameterComboBox" ||
                             selected == "CML_Numéros de place") &&
                            (targetName != "NumberFormatComboBox" ||
                             selected.StartsWith("001,002,003", StringComparison.Ordinal)))
                            CompleteStep(expectedIndex);
                    };
                    selector.SelectionChanged += handler;
                    _detachAction = () => selector.SelectionChanged -= handler;
                    if (targetName == "ActionFilterCombo" &&
                        IsDeletionAction(selector.SelectedItem?.ToString()))
                    {
                        _text.Text += UiLanguage.Get("DemoTourService.TheFilterIsAlreadyActiveClickNext");
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                    if (targetName == "VisualCardsList" && selector.SelectedItem != null)
                    {
                        _text.Text += UiLanguage.Get("DemoTourService.ACardIsAlreadySelectedExamineItThen");
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                    if ((targetName == "ParameterComboBox" &&
                         (selector.SelectedItem?.ToString() ?? "") == "CML_Numéros de place") ||
                        (targetName == "NumberFormatComboBox" &&
                         (selector.SelectedItem?.ToString() ?? "").StartsWith("001,002,003", StringComparison.Ordinal)))
                    {
                        _text.Text += UiLanguage.Get("DemoTourService.TheRightChoiceIsAlreadyActiveClickNext");
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                }
                else
                {
                    MouseButtonEventHandler handler = (_, __) => CompleteStep(expectedIndex);
                    target.AddHandler(UIElement.MouseLeftButtonUpEvent, handler, true);
                    _detachAction = () => target.RemoveHandler(UIElement.MouseLeftButtonUpEvent, handler);
                }
            }

            internal void CompleteRestoration(bool furnitureRestored)
            {
                if (_index < 0 || _index >= _steps.Length || _steps[_index].Target != "RestoreDeletedButton")
                    return;
                if (TourId == "history" && IsPreparedExercise && !furnitureRestored)
                {
                    _text.Text = UiLanguage.Get("DemoTourService.TheTwoCompleteTablesAreNotThereYet");
                    return;
                }
                CompleteStep(_index);
            }

            internal void CompleteParameterRestoration(bool exerciseRestored)
            {
                if (_index < 0 || _index >= _steps.Length || _steps[_index].Target != "RestoreParametersButton")
                    return;
                if (!exerciseRestored)
                {
                    _text.Text = UiLanguage.Get("DemoTourService.TheReferenceElementDoesNotHaveIts4");
                    return;
                }
                CompleteStep(_index);
            }

            private void CompleteStep(int expectedIndex)
            {
                if (_index != expectedIndex || _completed) return;
                _completed = true;
                if (_index + 1 == _steps.Length)
                {
                    _text.Text = _steps[_index].Target == "RestoreParametersButton"
                        ? UiLanguage.Get("DemoTourService.BothElementsAreBackAndTheReferenceElement")
                        : _steps[_index].Target == "RestoreDeletedButton"
                        ? UiLanguage.Get("DemoTourService.TheFurnitureIsBackBIMaestroSelectedItAnd")
                        : _steps[_index].CompletionEvent == "load-bureau-commun"
                        ? UiLanguage.Get("DemoTourService.RevitLoadedTheFamilyAndStartedPlacingIt")
                        : _steps[_index].CompletionEvent == "radial-tutorial-chaise-placed"
                        ? UiLanguage.Get("DemoTourService.YouPlacedAChairFromTheRadialMenu")
                        : _steps[_index].CompletionEvent == "clash-export-written"
                        ? UiLanguage.Get("DemoTourService.TheAnalysisConfirmedThatThePipeToPipe")
                        : UiLanguage.Get("DemoTourService.ActionConfirmedInTheModelYouCanFinish");
                    _next.IsEnabled = true;
                }
                else if (TourId == "clash-3d") _next.IsEnabled = true;
                else
                {
                    _index++;
                    ShowStep();
                }
            }

            private DispatcherTimer _favoriteHighlightTimer;
            private void RefreshFavoriteHighlight()
            {
                if (_favoriteHighlightTimer != null) return;
                _favoriteHighlightTimer = new DispatcherTimer(DispatcherPriority.Loaded, _window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
                _favoriteHighlightTimer.Tick += (_, __) => {
                    if (_index >= _steps.Length || _steps[_index].Target != "TutorialFavoriteStar") { _favoriteHighlightTimer.Stop(); _favoriteHighlightTimer = null; return; }
                    if (_adorner != null) _layer?.Remove(_adorner);
                    _adorner = null; _layer = null;
                    Highlight("TutorialFavoriteStar", _index);
                };
                _favoriteHighlightTimer.Start();
            }
            private void Highlight(string targetName, int expectedIndex, int attempt = 0)
            {
                if (_closed) return;
                if (_index != expectedIndex || !_window.IsVisible) return;
                if (targetName == "TutorialFavoriteStar") RefreshFavoriteHighlight();
                RemoveHighlight();
                FrameworkElement target;
                if (_window is Famille.FamilyBrowserWindow browser && targetName == "TutorialFavoriteStar")
                    target = browser.FindTutorialFavoriteStar();
                else if (_window is Famille.FamilyBrowserWindow folderBrowser && targetName == "TutorialMeetingFolderCard")
                    target = folderBrowser.FindTutorialMeetingFolderCard();
                else target = _window.FindName(targetName) as FrameworkElement;
                if (target == null || !target.IsVisible)
                {
                    if (targetName == "TutorialMeetingFolderCard" && attempt < 20)
                    {
                        var retry = new DispatcherTimer(DispatcherPriority.Loaded, _window.Dispatcher)
                            { Interval = TimeSpan.FromMilliseconds(100) };
                        retry.Tick += (_, __) =>
                        {
                            retry.Stop();
                            Highlight(targetName, expectedIndex, attempt + 1);
                        };
                        retry.Start();
                    }
                    return;
                }
                if (targetName == "TutorialFavoriteStar") RefreshFavoriteHighlight();
                target.BringIntoView();
                _layer = AdornerLayer.GetAdornerLayer(target);
                if (_layer == null) return;
                _adorner = new TargetAdorner(target);
                _layer.Add(_adorner);
            }

            private void RemoveHighlight()
            {
                if (_adorner != null) _layer?.Remove(_adorner);
                _adorner = null; _layer = null;
            }

            internal void Close()
            {
                if (_closed) return;
                _closed = true;
                if (TourId == "history" && _window is Analyse.ElementHistoryWindow) Analyse.ElementHistoryTracker.CaptureDetailedDeletedMesh = false;
                _favoriteHighlightTimer?.Stop(); _favoriteHighlightTimer = null;
                _window.Loaded -= OnLoaded;
                _detachAction?.Invoke();
                _detachAction = null;
                RemoveHighlight();
                if (_root != null && _root.Children.Contains(_card)) _root.Children.Remove(_card);
                if (ActiveGuides.TryGetValue(_window, out DemoWindowGuide active) && ReferenceEquals(active, this))
                    ActiveGuides.Remove(_window);
                if (!_windowClosed && _window is Famille.FamilyBrowserWindow browser)
                    browser.EndTutorialCatalog();
                if (_window is Analyse.SmartCheckWindow clash) clash.EndTutorial();
            }
        }

        // Libellé « Suppression » / « Deletion » selon la langue de l'interface.
        private static bool IsDeletionAction(string action) =>
            !string.IsNullOrEmpty(action) &&
            (action.IndexOf("supp", StringComparison.OrdinalIgnoreCase) >= 0 ||
             action.IndexOf("delet", StringComparison.OrdinalIgnoreCase) >= 0);

        private sealed class TargetAdorner : Adorner
        {
            internal TargetAdorner(UIElement target) : base(target)
            { IsHitTestVisible = false; Focusable = false; }
            protected override void OnRender(DrawingContext drawing)
            {
                var bounds = new Rect(AdornedElement.RenderSize);
                if (bounds.Width > 0 && bounds.Height > 0)
                    drawing.DrawRoundedRectangle(null, new Pen(DemoTourPalette.Accent, 3),
                        new Rect(-2, -2, bounds.Width + 4, bounds.Height + 4), 5, 5);
            }
        }
    }

    internal static class DemoTourCompletion
    {
        private static string _nextTour;
        private static readonly string[] Order = { "reservation", "history", "colors", "pipe-calculation", "organizer", "view-template", "excel", "family-browser", "mep-booster", "clash-3d" };
        private static string[] Labels => new[] { UiLanguage.Get("DemoTourService.AutoOpening"), UiLanguage.Get("DemoTourService.WhoDidThat"), UiLanguage.Get("DemoTourService.ColorsAndViews"), UiLanguage.Get("DemoTourService.PipeCalculation"), UiLanguage.Get("DemoTourService.Organizer"), UiLanguage.Get("DemoTourService.ViewTemplate"), UiLanguage.Get("DemoTourService.ExcelManager"), UiLanguage.Get("DemoTourService.FamilyBrowser"), "MEP Booster", "Clash 3D" };
        internal static void Show(IntPtr owner, string completed, string summary, Action beforeNext = null)
        {
            TutorialAnalytics.Complete(completed);
            int index = Array.IndexOf(Order, completed);
            int next = index < 0 || index + 1 == Order.Length ? 0 : index + 1;
            bool continueTour = DemoTourMessage.Show(owner, UiLanguage.Get("DemoTourService.TutorialComplete"),
                summary + UiLanguage.Get("DemoTourService.MoreTutorialsAreAvailableDoYouWantTo") + Labels[next] + UiLanguage.Get("DemoTourService.EstimatedTime") + DemoTourCatalog.EstimatedDurations[Order[next]] + ".",
                UiLanguage.Get("DemoTourService.NextTutorial"), UiLanguage.Get("DemoTourService.FinishForNow"), primaryButtonIsAction: true);
            if (!continueTour) return;
            beforeNext?.Invoke();
            _nextTour = Order[next];
        }
        internal static bool ProcessIdling(UIApplication app)
        {
            if (_nextTour == null) return false;
            string tour = _nextTour;
            _nextTour = null;
            string message = null;
            try { DemoToursCommand.RunChoice(app, tour, ref message); }
            catch (Exception ex) { DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.Get("DemoTourService.TheTutorialCouldNotStart2"), ex.Message); }
            return true;
        }
    }

    internal sealed class DemoChoiceWindow : Window
    {
        internal string Choice { get; private set; }
        internal DemoChoiceWindow()
        {
            Title = UiLanguage.Get("DemoTourService.BIMaestroGuidedTours");
            Width = 470; Height = 510; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; Background = Brushes.White;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Content = scroll;
            var stack = new StackPanel { Margin = new Thickness(20) }; scroll.Content = stack;
            stack.Children.Add(new TextBlock { Text = UiLanguage.Get("DemoTourService.LearnBIMaestroWithBulbasaur"), FontSize = 18,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            stack.Children.Add(new TextBlock { Text = UiLanguage.Get("DemoTourService.DiscoverTheCommandsThenTryThemInThe"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            Button quick = AddChoice(stack, UiLanguage.Get("DemoTourService.StartTheOverview2Min"), "overview");
            quick.FontWeight = FontWeights.SemiBold;
            quick.BorderBrush = DemoTourPalette.Accent;
            quick.BorderThickness = new Thickness(2);
            AddChoice(stack, UiLanguage.Get("DemoTourService.CreateAndOpenTheTrainingModel"), "create");
            var detailed = new Expander { Header = UiLanguage.Get("DemoTourService.GoStraightToTheDetailedExercises"),
                Margin = new Thickness(0, 6, 0, 12), IsExpanded = false };
            stack.Children.Add(detailed);
            var detailChoices = new StackPanel { Margin = new Thickness(8, 10, 0, 0) };
            detailed.Content = detailChoices;
            AddChoice(detailChoices, UiLanguage.Get("DemoTourService.1AutoOpening"), "reservation");
            AddChoice(detailChoices, UiLanguage.Get("DemoTourService.2WhoDidThat"), "history");
            AddChoice(detailChoices, UiLanguage.Get("DemoTourService.3ColorsAndViews"), "colors");
            AddChoice(detailChoices, UiLanguage.Get("DemoTourService.4PipeCalculation"), "pipe-calculation");
            AddChoice(detailChoices, UiLanguage.Get("DemoTourService.5Organizer"), "organizer");
            AddChoice(detailChoices, UiLanguage.Get("DemoTourService.6ViewTemplate"), "view-template");
            AddChoice(detailChoices, UiLanguage.Get("DemoTourService.7ExcelManager"), "excel");
            AddChoice(detailChoices, "9 · MEP Booster", "mep-booster");
            AddChoice(detailChoices, UiLanguage.Get("DemoTourService.8FamilyBrowser"), "family-browser");
            AddChoice(detailChoices, "10 · Clash 3D", "clash-3d");
            AddChoice(stack, UiLanguage.Get("DemoTourService.RestartTheModelExercises"), "reset");
        }
        private Button AddChoice(Panel panel, string label, string id)
        {
            if (DemoTourCatalog.EstimatedDurations.TryGetValue(id, out string duration))
                label += " · " + duration;
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 0, 7),
                Padding = new Thickness(10, 6, 10, 6), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, __) => { Choice = id; Close(); };
            panel.Children.Add(button);
            return button;
        }
    }

    internal class DemoDiscoveryWindow : Window
    {
        internal string SelectedTour { get; private set; }

        internal DemoDiscoveryWindow()
        {
            Title = UiLanguage.Get("DemoTourService.BIMaestroQuickOverview");
            Width = 570; Height = 710; MinWidth = 480; MinHeight = 550;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false; Background = Brushes.White;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Content = scroll;
            var stack = new StackPanel { Margin = new Thickness(22) };
            scroll.Content = stack;
            stack.Children.Add(new TextBlock { Text = UiLanguage.Get("DemoTourService.ChooseAGuidedTour"), FontSize = 21,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            stack.Children.Add(new TextBlock { Text = UiLanguage.Get("DemoTourService.ChooseATutorialClickItsCardToStart"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
            AddCard(stack, UiLanguage.Get("DemoTourService.1AutoOpening"),
                UiLanguage.Get("DemoTourService.WhenASystemPassesThroughAWallBIMaestro"));
            AddCard(stack, UiLanguage.Get("DemoTourService.2WhoDidThat"),
                UiLanguage.Get("DemoTourService.FindWhoMadeAChangeAndInWhat"));
            AddCard(stack, UiLanguage.Get("DemoTourService.3ColorsAndViews"),
                UiLanguage.Get("DemoTourService.CustomizeTheProjectBrowserWithoutRenamingOrRecreating"));
            AddCard(stack, UiLanguage.Get("DemoTourService.4PipeCalculation"),
                UiLanguage.Get("DemoTourService.SelectTwoPipingSystemsWithElbowsAndA"));
            AddCard(stack, UiLanguage.Get("DemoTourService.5Organizer"),
                UiLanguage.Get("DemoTourService.NumberEightCMLParkingSpacesOnTwoLevels"));
            AddCard(stack, UiLanguage.Get("DemoTourService.6ViewTemplate"),
                UiLanguage.Get("DemoTourService.ExportTheSettingsOfA3DViewThen"));
            AddCard(stack, UiLanguage.Get("DemoTourService.7ExcelManager"),
                UiLanguage.Get("DemoTourService.ExportTheCMLParkingSpacesEditableNumberAnd"));
            AddCard(stack, UiLanguage.Get("DemoTourService.8FamilyBrowser"),
                UiLanguage.Get("DemoTourService.ExploreACatalogOf35FamiliesOrganizedIn"));
            AddCard(stack, "9 · MEP Booster", UiLanguage.Get("DemoTourService.RotateAValveLookAtTheResultAnd"));
            AddCard(stack, "10 · Clash 3D", UiLanguage.Get("DemoTourService.AnalyzeTwoIntersectionsRaiseATestPipeCreate"));
            var close = new Button { Content = UiLanguage.Get("DemoTourService.FinishForNow2"), Padding = new Thickness(12, 7, 12, 7),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
            close.Click += (_, __) => Close();
            stack.Children.Add(close);
        }

        private void AddExerciseButton(Panel parent, string label, string tourId)
        {
            var button = new Button { Content = label, Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, __) => { SelectedTour = tourId; Close(); };
            parent.Children.Add(button);
        }

        private void AddCard(Panel parent, string title, string description)
        {
            string[] ids = { "reservation", "history", "colors", "pipe-calculation", "organizer", "view-template", "excel", "family-browser", "mep-booster", "clash-3d" };
            string[] icons = { "Auto réservation.png", "qui à fait ça (2).png", "Couleur oui non.png", "Calcul de canalisation.png", "Organisateur.png", "Gabarit de vue simple.png", "Gestion Excel.png", "FamilyBrowser.png", "MEP Booster vanne rotation.png", "Clash 3D.png" };
            int index = int.Parse(title.Substring(0, title.IndexOf('·')).Trim()) - 1;
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var image = new Image { Width = 42, Height = 42, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 12, 0) };
            using (var stream = typeof(DemoDiscoveryWindow).Assembly.GetManifestResourceStream(typeof(DemoDiscoveryWindow).Assembly.GetManifestResourceNames().First(name => name.EndsWith("." + icons[index], StringComparison.OrdinalIgnoreCase))))
            {
                if (stream != null) { var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); image.Source = bitmap; }
            }
            row.Children.Add(image);
            var body = new StackPanel(); Grid.SetColumn(body, 1); row.Children.Add(body);
            body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = DemoTourPalette.Accent });
            body.Children.Add(new TextBlock { Text = UiLanguage.Get("DemoTourService.EstimatedTime2") + DemoTourCatalog.EstimatedDurations[ids[index]],
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            body.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 8) });
            body.Children.Add(new TextBlock { Text = UiLanguage.Get("DemoTourService.StartThisTutorial"), FontWeight = FontWeights.SemiBold, Foreground = DemoTourPalette.Accent });
            var button = new Button { Content = row, Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12), HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(1), Background = Brushes.White };
            button.Click += (_, __) => { SelectedTour = ids[index]; Close(); };
            parent.Children.Add(button);
        }
    }

    internal static class DemoTourMessage
    {
        internal static bool Show(IntPtr owner, string heading, string message,
            string buttonLabel = null, string learnMoreLabel = null, bool primaryButtonIsAction = false)
        {
            bool learnMore = false;
            var window = new Window
            {
                Title = UiLanguage.Get("DemoTourService.BIMaestroBulbasaur"),
                Width = 480,
                MinWidth = 380,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 620,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = Brushes.White
            };
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(window).Owner = owner;

            var root = new StackPanel();
            window.Content = root;
            var header = new Border
            {
                Background = DemoTourPalette.Highlight,
                BorderBrush = DemoTourPalette.Accent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(18, 14, 18, 14)
            };
            root.Children.Add(header);
            var headingRow = new StackPanel { Orientation = Orientation.Horizontal };
            header.Child = headingRow;
            headingRow.Children.Add(new Image
            {
                Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                Width = 48,
                Height = 48,
                Margin = new Thickness(0, 0, 14, 0)
            });
            headingRow.Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 365
            });
            var content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 400,
                Padding = new Thickness(20, 18, 20, 14)
            };
            root.Children.Add(content);
            content.Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                LineHeight = 22,
                Foreground = Brushes.Black
            };
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 4, 20, 18)
            };
            root.Children.Add(footer);
            if (!string.IsNullOrWhiteSpace(learnMoreLabel))
            {
                var more = new Button
                {
                    Content = learnMoreLabel,
                    Padding = new Thickness(14, 7, 14, 7),
                    Margin = new Thickness(0, 0, 10, 0),
                    FontWeight = FontWeights.SemiBold,
                    Background = Brushes.White,
                    BorderBrush = DemoTourPalette.Accent,
                    BorderThickness = new Thickness(2),
                    IsCancel = primaryButtonIsAction
                };
                more.Click += (_, __) => { learnMore = !primaryButtonIsAction; window.Close(); };
                footer.Children.Add(more);
            }
            var close = new Button
            {
                Content = buttonLabel ?? UiLanguage.Get("DemoTourService.Continue"),
                MinWidth = 125,
                Padding = new Thickness(14, 7, 14, 7),
                FontWeight = FontWeights.SemiBold,
                Background = primaryButtonIsAction ? DemoTourPalette.Accent : DemoTourPalette.Highlight,
                Foreground = primaryButtonIsAction ? Brushes.White : Brushes.Black,
                BorderBrush = DemoTourPalette.Accent,
                BorderThickness = new Thickness(2),
                IsDefault = primaryButtonIsAction
            };
            close.Click += (_, __) => { learnMore = primaryButtonIsAction; window.Close(); };
            footer.Children.Add(close);
            window.ShowDialog();
            return learnMore;
        }
    }

    internal static class DemoTourDeepDive
    {
        internal static void Show(IntPtr owner, string tourId)
        {
            string heading;
            string[,] topics;
            switch (tourId)
            {
                case "reservation":
                    heading = UiLanguage.Get("DemoTourService.AutoOpeningUnderstandTheSettings");
                    topics = new[,]
                    {
                        { UiLanguage.Get("DemoTourService.1ChooseTheCase"), UiLanguage.Get("DemoTourService.InFamiliesSelectTheHostWallOrFloor") },
                        { UiLanguage.Get("DemoTourService.2AddYourRFA"), UiLanguage.Get("DemoTourService.IfYourFamilyIsAlreadyLoadedInThe") },
                        { UiLanguage.Get("DemoTourService.3MapTheParameters"), UiLanguage.Get("DemoTourService.MapTheCalculatedDimensionsToTheFamilyS") },
                        { UiLanguage.Get("DemoTourService.4AdjustThePlacement"), UiLanguage.Get("DemoTourService.StartWithAnAutomaticVerticalReferenceAndA") },
                        { UiLanguage.Get("DemoTourService.5CheckTheResult"), UiLanguage.Get("DemoTourService.AfterCreationSelectTheOpeningCheckHowIt") }
                    };
                    break;
                case "history":
                    heading = UiLanguage.Get("DemoTourService.WhoDidThatReadAndRestore");
                    topics = new[,]
                    {
                        { UiLanguage.Get("DemoTourService.1NarrowTheSearch"), UiLanguage.Get("DemoTourService.FilterByActionUserTextOrPeriodIf") },
                        { UiLanguage.Get("DemoTourService.2ReadTheEvidence"), UiLanguage.Get("DemoTourService.InDetailsCompareTheAuthorDateCategoryAnd") },
                        { UiLanguage.Get("DemoTourService.3Preview"), UiLanguage.Get("DemoTourService.VisualizeShowsTheEstimatedLocationWithoutChangingThe") },
                        { UiLanguage.Get("DemoTourService.4RecreateTheElements"), UiLanguage.Get("DemoTourService.RestoreElementsRecreatesDeletedElementsTheFamilyType") },
                        { UiLanguage.Get("DemoTourService.5RestoreParameters"), UiLanguage.Get("DemoTourService.SelectAParameterChangeRowCompareBeforeAfter") }
                    };
                    break;
                default:
                    heading = UiLanguage.Get("DemoTourService.ColorsAndViewsUnderstandTheRules");
                    topics = new[,]
                    {
                        { UiLanguage.Get("DemoTourService.1PanelsAndProjectBrowser"), UiLanguage.Get("DemoTourService.BIMaestroPanelColorsAndTheProjectBrowserStyle") },
                        { UiLanguage.Get("DemoTourService.2BackgroundAndFolders"), UiLanguage.Get("DemoTourService.TheBackgroundSettingAffectsTheProjectBrowserBackground") },
                        { UiLanguage.Get("DemoTourService.3IconsByName"), UiLanguage.Get("DemoTourService.InIconsEnableTheDisplayThenAssociateAn") },
                        { UiLanguage.Get("DemoTourService.4CheckInRevit"), UiLanguage.Get("DemoTourService.SaveAndLookAtTheActualProjectBrowser") }
                    };
                    break;
            }

            var window = new Window
            {
                Title = UiLanguage.Get("DemoTourService.BIMaestroBulbasaurGoesDeeper"),
                Width = 600,
                Height = 570,
                MinWidth = 480,
                MinHeight = 430,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = Brushes.White
            };
            if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
            var root = new DockPanel();
            window.Content = root;
            var footer = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            var close = new Button { Content = UiLanguage.Get("DemoTourService.BackToTheModel"), Padding = new Thickness(16, 8, 16, 8),
                Background = DemoTourPalette.Highlight,
                BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(2) };
            close.Click += (_, __) => window.Close();
            footer.Children.Add(close);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(scroll);
            var stack = new StackPanel { Margin = new Thickness(22) };
            scroll.Content = stack;
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 16) };
            stack.Children.Add(titleRow);
            titleRow.Children.Add(new Image { Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                Width = 44, Height = 44, Margin = new Thickness(0, 0, 12, 0) });
            titleRow.Children.Add(new TextBlock { Text = heading, FontSize = 20,
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 490 });
            for (int i = 0; i < topics.GetLength(0); i++)
            {
                var card = new Border { BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10), Background = Brushes.WhiteSmoke,
                    Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12) };
                stack.Children.Add(card);
                var body = new StackPanel(); card.Child = body;
                body.Children.Add(new TextBlock { Text = topics[i, 0], FontSize = 15,
                    FontWeight = FontWeights.SemiBold });
                body.Children.Add(new TextBlock { Text = topics[i, 1], TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0), LineHeight = 21 });
            }
            window.ShowDialog();
        }
    }
}
