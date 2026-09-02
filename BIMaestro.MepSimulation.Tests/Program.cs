using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media.Media3D;

namespace BIMaestro.VideoGames
{
    internal static class Program
    {
        private static int _assertions;

        private static int Main(string[] args)
        {
            try
            {
                if ((args.Length == 4 || args.Length == 5) &&
                    string.Equals(args[0], "--toggle-replay", StringComparison.OrdinalIgnoreCase))
                {
                    long elementId = 0;
                    if (!long.TryParse(args[2], out elementId))
                        throw new ArgumentException("Identifiant de vanne invalide.");
                    bool close = string.Equals(
                        args[3], "close", StringComparison.OrdinalIgnoreCase);
                    if (!close && !string.Equals(
                            args[3], "open", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("État attendu : open ou close.");
                    }
                    string baseline = args.Length == 5 ? args[4] : "current";
                    return ToggleAndReplayExportedGraph(
                        args[1], elementId, close, baseline);
                }
                if (args.Length >= 2 && args.Length <= 3 &&
                    string.Equals(args[0], "--replay", StringComparison.OrdinalIgnoreCase))
                {
                    long inspectedElementId = 0;
                    if (args.Length == 3)
                        long.TryParse(args[2], out inspectedElementId);
                    return ReplayExportedGraph(args[1], inspectedElementId);
                }
                StraightValveCutsOnlyPath();
                TeeSuppliesBothBranches();
                SmallInletFeedsLargeHeaderWithoutReversingIt();
                SmallDnCannotSplitAContinuousReturnHeader();
                SmallDnKeepsTwoPortFittingsAlignedWithHeader();
                NativePipeTapUsesDiameterRule();
                TwoSmallReturnsMergeIntoLargeCollector();
                ReducerAfterEqualTeeProvidesEffectiveCollectorDn();
                CollectorMergeSurvivesDownstreamTee();
                PumpOutletAnchorsLocalDnMerge();
                NativePumpPortsImposeAspirationAndDischarge();
                NativePumpSuctionOverridesSmallDnTeeInference();
                NativePumpSuctionCrossesPassiveChainToFirstTee();
                ParallelNativePumpDischargesDoNotReverseEachOther();
                ConflictingPumpVotesDoNotUsePumpCountAsFlowRate();
                OpeningValvePreservesEstablishedPumpConsensus();
                DirectionalEquipmentProtectsItsBranchFromAnotherSplit();
                LargeSupplyStillDistributesToTwoSmallerBranches();
                PumpBranchesKeepTheirEstablishedDirection();
                ExplicitSmallOutletPreventsMergeInference();
                SubthresholdDnDoesNotInferMerge();
                LocalOverrideDoesNotDisableNeighboringDnMerge();
                DnMergeInferenceSkipsRejoinedBypass();
                BalancedBypassAtOneNodeRemainsStagnant();
                HydraulicPotentialEliminatesIsolatedLoopReversal();
                TwoPortFittingFollowsAuthoritativeAdjacentPaths();
                TwoPortPipeAccessoryFollowsAuthoritativeAdjacentPaths();
                PassiveContinuityCannotCrossDirectedBoundaryStop();
                TeePortsFollowTheirAdjacentPipes();
                CollinearTeeChainKeepsOneHeaderDirection();
                LoopBypassesClosedValve();
                SecondSourceMaintainsSupply();
                DisconnectedBranchIsIsolated();
                ThreeWayValveCutsEveryOutlet();
                MissingDirectionsDoNotBreakReachability();
                DirectedPipeSourceSuppliesOnlyChosenSide();
                ArrivalAndReturnStabilizeDirection();
                OpenIsolationValveIsDirectionallyTransparent();
                SourceOnlyNetworkKeepsDirectionAcrossOpenValve();
                DifferentRevitSystemsDoNotExchangeDirection();
                SharedSystemAbbreviationBridgesInlineEquipment();
                SharedSystemAbbreviationDoesNotBridgeMultiPortEquipment();
                PipeJunctionBridgesPhysicallyConnectedRevitSystems();
                PumpConstraintBridgesDifferentRevitSystems();
                ReturnOnlySystemFlowsTowardDeclaredOutlet();
                ClosedValveCannotCreateImplicitReturnInlet();
                ClosedValveDeadEndStaysPressurizedWithoutCirculation();
                UnmarkedOpenEndKeepsCirculation();
                EqualOpposingArrivalsStayAmbiguous();
                ParallelBypassesKeepTheSameDirection();
                PumpConstraintDoesNotCreateSupply();
                ManualDirectionOverrideBeatsAutomaticPotential();
                LocalDirectionOverrideDoesNotReorientNeighbors();
                DirectionExplanationNamesSourceAndReturn();
                DirectionExplanationListsAlternativeSourcesDeterministically();
                PipeFittingCannotBecomeHydraulicSource();
                DirectionExplanationUsesCurrentRevitElementIdentity();
                DirectionExplanationReportsAlternativeLoop();
                DirectionExplanationReportsClosedValveLimit();
                DirectionExplanationMarksManualCorrection();
                HistoryRestoresEveryScenarioMutation();
                HistoryRestoresRemovedSource();
                HistoryMakesResetOneUndoableAction();
                HistoryClearsRedoAfterNewActionAndKeepsOneHundred();
                HistoryRefusesOperationsWhileCalculationRuns();
                HistoryFinalStateIsWhatPersistenceCaptures();
                PartialValveResetPreservesSourcesAndDirections();
                PartialSourceResetPreservesValves();
                SystemResetOnlyTouchesSelectedSystem();
                NetworkTraceFollowsSourceAndDownstream();
                NetworkTraceExposesAndSelectsTeeBranches();
                NetworkTraceBranchSelectionSurvivesRejoinedLoop();
                NetworkTraceKeepsBypassAroundClosedValve();
                NetworkTraceHonorsHiddenSystems();
                NetworkTraceStaysFastOnLargeGraph();
                DiagnosticsClassifyCriticalDirectionConflict();
                DiagnosticsDetectAmbiguousFlowControl();
                DiagnosticsDetectUnknownPassThroughComponent();
                DiagnosticsDetectDisconnectedElement();
                DiagnosticsGroupOpenConnectorsInSmartMode();
                DiagnosticsDetectBranchWithoutSource();
                DiagnosticsDetectIncompatibleSystems();
                DiagnosticsReportInvalidSavedSettings();
                NetworkWithoutSourceStaysUnknown();
                EmptyGraphDoesNotFail();
                ReplaySnapshotRoundTripPreservesGraphAndCalculation();
                ScenarioRoundTripRestoresSourcesAndValves();
                NamedScenariosCanBeSavedLoadedAndDeleted();
                ResetRemovesPersistedScenario();
                ChangedNetworkSkipsInvalidDirection();
                ChangedNetworkSkipsLegacyPipeFittingSource();
                ScenarioFilesAreIsolatedByModel();
                UnsavedModelPersistsOnlyInCurrentSession();
                SelectionIndexChoosesNearestPreciseTriangle();
                SelectionIndexRedirectsInsulationToPipe();
                SelectionIndexPrefersPreciseGeometryToBoundsFallback();
                SelectionIndexStaysWithinHoverBudget();
                GroundContactPrefersFloorOverMepClutter();
                GroundContactFallsBackToAnyWalkableSurface();
                GroundContactKeepsElevatedPlatformOverFloor();
                SupportedGroundRejectsIsolatedBump();
                SupportedGroundPreservesSlopeHeight();
                SupportedGroundRequiresSeveralContactPoints();
                Console.WriteLine("MEP graph regression tests: " + _assertions + " assertions passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                return 1;
            }
        }

        private static void GroundContactPrefersFloorOverMepClutter()
        {
            GameSceneData scene = CreateGroundContactScene(includeFloor: true);
            var world = new GameCollisionWorld(scene);

            bool found = world.TryFindGround(
                0.0,
                0.0,
                1.0,
                -1.0,
                out double ground);

            Assert(found, "Ground contact must find the preferred floor.");
            Assert(Math.Abs(ground) < 1e-8,
                "MEP clutter above a floor must not become a sequence of camera steps.");
        }

        private static void GroundContactFallsBackToAnyWalkableSurface()
        {
            GameSceneData scene = CreateGroundContactScene(includeFloor: false);
            var world = new GameCollisionWorld(scene);

            bool found = world.TryFindGround(
                0.0,
                0.0,
                1.0,
                -1.0,
                out double ground);

            Assert(found, "Ground contact must keep non-floor platforms usable.");
            Assert(Math.Abs(ground) < 1e-8,
                "The highest generic surface must remain available when no floor exists.");
        }

        private static void GroundContactKeepsElevatedPlatformOverFloor()
        {
            GameSceneData scene = CreateGroundContactScene(
                includeFloor: true,
                genericHeight: 1.2);
            var world = new GameCollisionWorld(scene);

            bool found = world.TryFindGround(
                0.0,
                0.0,
                2.0,
                -1.0,
                out double ground);

            Assert(found, "Ground contact must find the elevated platform.");
            Assert(Math.Abs(ground - 1.2) < 1e-8,
                "A generic platform higher than one step must not be ignored.");
        }

        private static GameSceneData CreateGroundContactScene(
            bool includeFloor,
            double genericHeight = 0.3)
        {
            var scene = new GameSceneData();
            if (includeFloor)
            {
                scene.Triangles.Add(new GameTriangle(
                    new Point3D(-2.0, -2.0, 0.0),
                    new Point3D(2.0, -2.0, 0.0),
                    new Point3D(-2.0, 2.0, 0.0),
                    new Vector3D(0.0, 0.0, 1.0),
                    true));
            }

            scene.Triangles.Add(new GameTriangle(
                new Point3D(-1.0, -1.0, genericHeight),
                new Point3D(1.0, -1.0, genericHeight),
                new Point3D(-1.0, 1.0, genericHeight),
                new Vector3D(0.0, 0.0, 1.0),
                false));
            scene.NormalizeCoordinates(
                new Point3D(0.0, 0.0, 6.0),
                new Vector3D(1.0, 0.0, -0.2));
            return scene;
        }

        private static void SupportedGroundRejectsIsolatedBump()
        {
            var scene = new GameSceneData();
            AddGroundQuad(scene, -2.0, -2.0, 2.0, 2.0,
                0.0, 0.0, 0.0, 0.0);
            AddGroundQuad(scene, -0.12, -0.12, 0.12, 0.12,
                0.30, 0.30, 0.30, 0.30);
            NormalizeGroundScene(scene);
            var world = new GameCollisionWorld(scene);

            bool found = world.TryFindSupportedGround(
                0.0, 0.0, 0.76, 1.0, -1.0,
                out double ground,
                out _);

            Assert(found, "The footprint must retain the surrounding floor support.");
            Assert(Math.Abs(ground) < 1e-8,
                "A small isolated facet must not lift the whole player capsule.");
        }

        private static void SupportedGroundPreservesSlopeHeight()
        {
            var scene = new GameSceneData();
            AddGroundQuad(scene, -2.0, -2.0, 2.0, 2.0,
                0.0, 0.8, 0.0, 0.8);
            NormalizeGroundScene(scene);
            var world = new GameCollisionWorld(scene);

            bool found = world.TryFindSupportedGround(
                0.0, 0.0, 0.76, 1.5, -1.0,
                out double ground,
                out _);

            Assert(found, "The footprint must find support on a slope.");
            Assert(Math.Abs(ground - 0.4) < 1e-8,
                "Symmetric footprint probes must preserve the slope height at the player center.");
        }

        private static void SupportedGroundRequiresSeveralContactPoints()
        {
            var scene = new GameSceneData();
            AddGroundQuad(scene, -0.12, -0.12, 0.12, 0.12,
                0.0, 0.0, 0.0, 0.0);
            NormalizeGroundScene(scene);
            var world = new GameCollisionWorld(scene);

            bool found = world.TryFindSupportedGround(
                0.0, 0.0, 0.76, 1.0, -1.0,
                out _,
                out _);

            Assert(!found,
                "A single contact under the center must not keep the player suspended.");
        }

        private static void AddGroundQuad(
            GameSceneData scene,
            double minX,
            double minY,
            double maxX,
            double maxY,
            double z00,
            double z10,
            double z01,
            double z11)
        {
            var a = new Point3D(minX, minY, z00);
            var b = new Point3D(maxX, minY, z10);
            var c = new Point3D(minX, maxY, z01);
            var d = new Point3D(maxX, maxY, z11);
            AddGroundTriangle(scene, a, b, c);
            AddGroundTriangle(scene, b, d, c);
        }

        private static void AddGroundTriangle(
            GameSceneData scene,
            Point3D a,
            Point3D b,
            Point3D c)
        {
            Vector3D normal = Vector3D.CrossProduct(b - a, c - a);
            normal.Normalize();
            scene.Triangles.Add(new GameTriangle(a, b, c, normal, false));
        }

        private static void NormalizeGroundScene(GameSceneData scene)
        {
            scene.NormalizeCoordinates(
                new Point3D(0.0, 0.0, 6.0),
                new Vector3D(1.0, 0.0, -0.2));
        }

        private static void StraightValveCutsOnlyPath()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("valve", 2, valve: true);
            fixture.AddElement("terminal", 1);
            fixture.Connect("source", 0, "valve", 0);
            fixture.Connect("valve", 1, "terminal", 0);
            fixture.Calculate();
            AssertState(fixture, "terminal", GameMepFlowState.Supplied);
            fixture.CloseValve("valve");
            fixture.Calculate();
            AssertState(fixture, "terminal", GameMepFlowState.Isolated);
            Assert(fixture.Graph.FindValve("valve")!.UpstreamState ==
                GameMepFlowState.Supplied, "The source side of a closed valve must stay supplied.");
            Assert(fixture.Graph.FindValve("valve")!.DownstreamState ==
                GameMepFlowState.Isolated, "The downstream side must report isolation.");
        }

        private static void TeeSuppliesBothBranches()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("tee", 3);
            fixture.AddElement("a", 1);
            fixture.AddElement("b", 1);
            fixture.Connect("source", 0, "tee", 0);
            fixture.Connect("tee", 1, "a", 0);
            fixture.Connect("tee", 2, "b", 0);
            fixture.Calculate();
            AssertState(fixture, "a", GameMepFlowState.Supplied);
            AssertState(fixture, "b", GameMepFlowState.Supplied);
        }

        private static void SmallInletFeedsLargeHeaderWithoutReversingIt()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("main-inlet", 1, source: true);
            fixture.AddElement("main-before", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("main-after", 2);
            fixture.AddElement("return", 1);
            fixture.AddElement("small-inlet", 1, source: true);
            fixture.AddElement("small-branch", 2);

            fixture.Connect("main-inlet", 0, "main-before", 0);
            fixture.Connect("main-before", 1, "junction", 0);
            fixture.Connect("junction", 1, "main-after", 0);
            fixture.Connect("main-after", 1, "return", 0);
            // L'ordre géométrique du petit tuyau est volontairement inversé :
            // son chemin interne va du té vers l'arrivée. Le résultat visuel
            // doit dépendre de l'hydraulique et jamais de l'ordre Revit.
            fixture.Connect("small-inlet", 0, "small-branch", 1);
            fixture.Connect("small-branch", 0, "junction", 2);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);
            fixture.SetPortDiameter("junction", 0, 300.0);
            fixture.SetPortDiameter("junction", 1, 300.0);
            fixture.SetPortDiameter("junction", 2, 80.0);
            fixture.AddJunctionPaths("junction");
            fixture.SetPathLength("main-before", 8.0);
            fixture.SetPathLength("main-after", 8.0);
            fixture.SetPathLength("small-branch", 6.0);

            fixture.Calculate();

            Assert(fixture.Path("main-before").HasCirculation &&
                fixture.Path("main-before").FlowForward &&
                fixture.Path("main-after").HasCirculation &&
                fixture.Path("main-after").FlowForward,
                "A small inlet must not reverse the large header.");
            Assert(fixture.Path("small-branch").HasCirculation &&
                !fixture.Path("small-branch").FlowForward,
                "The small inlet must circulate all the way to the header.");
            AssertRenderableFlow(fixture, "small-branch");
            Assert(fixture.Path("small-branch").DirectionState ==
                    GameMepDirectionState.Resolved,
                "The visible small pipe must stay resolved without overriding an already coherent direction.");
            Assert(fixture.JunctionPath("junction", 0).FlowForward &&
                !fixture.JunctionPath("junction", 1).FlowForward &&
                fixture.JunctionPath("junction", 2).FlowForward,
                "Both inlets must converge at the junction and leave through the header.");
            Assert(fixture.JunctionPath("junction", 2).DirectionReason.IndexOf(
                    "DN", StringComparison.OrdinalIgnoreCase) >= 0,
                "A large diameter contrast must be reported by the junction rule.");
            Assert(fixture.Graph.DiameterDirectedPathCount == 0 &&
                new[] { "main-before", "main-after", "small-branch" }.All(key =>
                    fixture.Path(key).DirectionReason.IndexOf(
                        "jonction de DN", StringComparison.OrdinalIgnoreCase) < 0),
                "A coherent header and inlet must not have their resolved pipe directions overwritten by DN.");
        }

        private static void NativePipeTapUsesDiameterRule()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("main-inlet", 1, source: true);
            fixture.AddElement("host-pipe", 3);
            fixture.AddElement("main-after", 2);
            fixture.AddElement("outlet", 1);
            fixture.AddElement("branch-inlet", 1, source: true);
            fixture.AddElement("branch", 2);

            fixture.Connect("main-inlet", 0, "host-pipe", 0);
            fixture.Connect("host-pipe", 1, "main-after", 0);
            fixture.Connect("main-after", 1, "outlet", 0);
            fixture.Connect("branch-inlet", 0, "branch", 0);
            fixture.Connect("branch", 1, "host-pipe", 2);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.SetPortDiameter("host-pipe", 0, 300.0);
            fixture.SetPortDiameter("host-pipe", 1, 300.0);
            // Les piquages Revit natifs exposent parfois un port Curve sans DN.
            // La canalisation de branche doit alors fournir son DN effectif.
            fixture.SetPortDiameter("host-pipe", 2, 0.0);
            fixture.SetPipeDiameter("main-after", 300.0);
            fixture.SetPipeDiameter("branch", 80.0);
            fixture.AddNativeTapPath("host-pipe", 0, 1, 8.0);
            fixture.SetPathLength("main-after", 8.0);
            fixture.SetPathLength("branch", 6.0);

            fixture.Calculate();

            Assert(fixture.Graph.DiameterAwareJunctionCount == 1,
                "A host pipe carrying a native Curve tap must participate in the DN rule.");
            Assert(fixture.Path("host-pipe").FlowForward &&
                fixture.Path("branch").FlowForward,
                "The native tap injection must join the large host pipe without reversing it.");
            AssertRenderableFlow(fixture, "host-pipe");
            AssertRenderableFlow(fixture, "branch");
            AssertRenderableFlow(fixture, "main-after");
        }

        private static void SmallDnCannotSplitAContinuousReturnHeader()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("far-left", 2);
            fixture.AddElement("left-elbow", 2);
            fixture.AddElement("crossing", 4);
            fixture.AddElement("left-reducer", 2);
            fixture.AddElement("left-main", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("right-main", 2);
            fixture.AddElement("right-outlet", 1);
            fixture.AddElement("small-inlet", 1, source: true);
            fixture.AddElement("small-valve", 2, valve: true);
            fixture.AddElement("small-branch", 2);
            fixture.AddElement("cross-side-a", 2);
            fixture.AddElement("cross-side-b", 2);

            // Le DN300 de gauche continue hors de la zone modélisée. Son bout
            // libre est l'amont aspiré et traverse d'abord un autre croisement.
            fixture.Connect("far-left", 1, "left-elbow", 0);
            fixture.Connect("left-elbow", 1, "crossing", 1);
            fixture.Connect("crossing", 0, "left-reducer", 0);
            fixture.Connect("left-reducer", 1, "left-main", 0);
            fixture.Connect("crossing", 2, "cross-side-a", 0);
            fixture.Connect("crossing", 3, "cross-side-b", 0);
            fixture.Connect("left-main", 1, "junction", 0);
            fixture.Connect("junction", 1, "right-main", 0);
            fixture.Connect("right-main", 1, "right-outlet", 0);
            fixture.Connect("small-inlet", 0, "small-valve", 0);
            fixture.Connect("small-valve", 1, "small-branch", 0);
            fixture.Connect("small-branch", 1, "junction", 2);
            fixture.AddBoundary("right-outlet", GameMepBoundaryKind.Outlet);
            fixture.SetPortDiameter("junction", 0, 300.0);
            fixture.SetPortDiameter("junction", 1, 300.0);
            fixture.SetPortDiameter("junction", 2, 200.0);
            for (int port = 0; port < 4; port++)
                fixture.SetPortDiameter("crossing", port, 300.0);
            fixture.SetPortDirection("crossing", 0, 1.0, 0.0, 0.0);
            fixture.SetPortDirection("crossing", 1, -1.0, 0.0, 0.0);
            fixture.SetPortDirection("crossing", 2, 0.0, 1.0, 0.0);
            fixture.SetPortDirection("crossing", 3, 0.0, -1.0, 0.0);
            fixture.AddJunctionPaths("junction");
            fixture.AddJunctionPaths("crossing");
            fixture.SetPathLength("far-left", 8.0);
            fixture.SetPathLength("left-elbow", 2.0);
            fixture.SetPathLength("left-reducer", 2.0);
            fixture.SetPathLength("left-main", 8.0);
            fixture.SetPathLength("right-main", 8.0);
            fixture.SetPathLength("small-branch", 6.0);
            fixture.SetPathLength("cross-side-a", 6.0);
            fixture.SetPathLength("cross-side-b", 6.0);

            fixture.CloseValve("small-valve");
            fixture.Calculate();
            bool closedLeftDirection = fixture.Path("left-main").FlowForward;
            bool closedFarDirection = fixture.Path("far-left").FlowForward;
            Assert(closedFarDirection && closedLeftDirection &&
                fixture.Path("right-main").FlowForward,
                "With the DN 200 valve closed, the DN 300 return must flow continuously through the preceding crossing.");
            Assert(fixture.Graph.StableHeaderDirections.TryGetValue(
                    "junction", out Dictionary<int, bool>? remembered) &&
                remembered.Count == 2,
                "Closing the DN 200 valve must memorize the stable DN 300 header direction.");

            fixture.Graph.FindValve("small-valve")!.IsClosed = false;
            fixture.Calculate();

            Assert(fixture.Path("far-left").FlowForward &&
                fixture.Path("left-main").FlowForward &&
                fixture.Path("right-main").FlowForward &&
                fixture.Path("small-branch").FlowForward,
                "Opening the DN 200 valve must join the return without reversing the left DN 300.");
            Assert(fixture.Path("left-main").FlowForward == closedLeftDirection,
                "Opening or closing the DN 200 valve must never change the DN 300 header direction.");
            Assert(fixture.Path("far-left").FlowForward == closedFarDirection,
                "The stable DN 300 direction must propagate beyond the next crossing.");
            Assert(fixture.Path("left-elbow").FlowForward &&
                fixture.Path("left-reducer").FlowForward,
                "Two-port pipe fittings must stay aligned with the protected DN 300 header.");
            Assert(!fixture.JunctionPath("crossing", 0).FlowForward &&
                fixture.JunctionPath("crossing", 1).FlowForward,
                "The two aligned DN 300 ports of the crossing must follow the propagated backbone direction.");
            Assert(fixture.JunctionPath("crossing", 2).FlowForward ==
                    !fixture.Path("cross-side-a").FlowForward &&
                fixture.JunctionPath("crossing", 3).FlowForward ==
                    !fixture.Path("cross-side-b").FlowForward,
                "Each lateral crossing port must agree locally with its own neighboring pipe.");
            Assert(fixture.JunctionPath("junction", 0).FlowForward &&
                !fixture.JunctionPath("junction", 1).FlowForward &&
                fixture.JunctionPath("junction", 2).FlowForward,
                "At the tee, the left DN 300 and DN 200 must enter while the right DN 300 remains the only outlet.");
            AssertRenderableFlow(fixture, "left-main");
            AssertRenderableFlow(fixture, "far-left");
            AssertRenderableFlow(fixture, "left-elbow");
            AssertRenderableFlow(fixture, "left-reducer");
            AssertRenderableFlow(fixture, "right-main");
            AssertRenderableFlow(fixture, "small-branch");
        }

        private static void SmallDnKeepsTwoPortFittingsAlignedWithHeader()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("far-left", 2);
            fixture.AddElement("elbow", 2);
            fixture.AddElement("reducer", 2);
            fixture.AddElement("left-main", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("right-main", 2);
            fixture.AddElement("right-outlet", 1);
            fixture.AddElement("small-inlet", 1, source: true);
            fixture.AddElement("small-valve", 2, valve: true);
            fixture.AddElement("small-branch", 2);

            fixture.Connect("far-left", 1, "elbow", 0);
            fixture.Connect("elbow", 1, "reducer", 0);
            fixture.Connect("reducer", 1, "left-main", 0);
            fixture.Connect("left-main", 1, "junction", 0);
            fixture.Connect("junction", 1, "right-main", 0);
            fixture.Connect("right-main", 1, "right-outlet", 0);
            fixture.Connect("small-inlet", 0, "small-valve", 0);
            fixture.Connect("small-valve", 1, "small-branch", 0);
            fixture.Connect("small-branch", 1, "junction", 2);
            fixture.AddBoundary("right-outlet", GameMepBoundaryKind.Outlet);
            fixture.SetPortDiameter("junction", 0, 300.0);
            fixture.SetPortDiameter("junction", 1, 300.0);
            fixture.SetPortDiameter("junction", 2, 200.0);
            fixture.SetPipeDiameter("far-left", 300.0);
            fixture.SetPipeDiameter("left-main", 300.0);
            fixture.SetPipeDiameter("right-main", 300.0);
            fixture.SetPipeDiameter("small-branch", 200.0);
            fixture.AddJunctionPaths("junction");
            foreach (string key in new[]
            {
                "far-left", "elbow", "reducer", "left-main",
                "right-main", "small-branch"
            })
            {
                fixture.SetPathLength(key, 6.0);
            }

            fixture.CloseValve("small-valve");
            fixture.Calculate();
            fixture.Graph.FindValve("small-valve")!.IsClosed = false;
            fixture.Calculate();

            foreach (string key in new[]
            {
                "far-left", "elbow", "reducer", "left-main", "right-main"
            })
            {
                Assert(fixture.Path(key).FlowForward,
                    "A two-port fitting on the protected DN 300 header must keep the same continuous direction: " + key);
                AssertRenderableFlow(fixture, key);
            }
        }

        private static int ReplayExportedGraph(
            string filePath,
            long inspectedElementId)
        {
            GameMepReplaySnapshot snapshot = GameMepReplayStore.Load(filePath);
            GameMepReplayResult result = GameMepReplayStore.Replay(snapshot);
            Console.WriteLine("Cas MEP : " + snapshot.DocumentLabel);
            Console.WriteLine(
                snapshot.Graph.Elements.Count + " éléments, " +
                snapshot.Graph.Connectors.Count + " connecteurs, " +
                result.PathCount + " chemins.");
            Console.WriteLine(
                result.ReversedPathCount + " sens modifiés par le rejeu, " +
                result.StateChangeCount + " états modifiés.");
            Console.WriteLine(
                "Ruptures de continuité visibles : " +
                result.CapturedVisibleDiscontinuityCount + " avant, " +
                result.ReplayedVisibleDiscontinuityCount + " après.");
            foreach (string discontinuity in result.ReplayedVisibleDiscontinuities)
                Console.WriteLine("  rupture restante : " + discontinuity);
            if (inspectedElementId > 0)
            {
                GameMepElementData? inspected = snapshot.Graph.FindElement(
                    inspectedElementId);
                Console.WriteLine(
                    "Inspection " + inspectedElementId + " : " +
                    (inspected?.Name ?? "introuvable"));
                if (inspected != null)
                {
                    foreach (GameMepPathData path in inspected.Paths)
                    {
                        Console.WriteLine(
                            "  " + path.StartConnector + " -> " +
                            path.EndConnector + ", sens=" +
                            (path.FlowForward ? "avant" : "arrière") +
                            ", circulation=" + path.HasCirculation +
                            ", raison=" + path.DirectionReason);
                    }
                }
            }
            foreach (GameMepReplayDifference difference in
                result.Differences.Take(200))
            {
                Console.WriteLine(
                    "- " + difference.ElementName + " [" + difference.ElementId +
                    "] chemin " + difference.PathOrdinal + " : " +
                    difference.CapturedState + " -> " + difference.ReplayedState);
            }
            if (result.Differences.Count > 200)
            {
                Console.WriteLine(
                    "... " + (result.Differences.Count - 200) +
                    " différences supplémentaires.");
            }
            return 0;
        }

        private static int ToggleAndReplayExportedGraph(
            string filePath,
            long valveElementId,
            bool close,
            string baseline)
        {
            GameMepReplaySnapshot snapshot = GameMepReplayStore.Load(filePath);
            if (!string.Equals(baseline, "current", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(baseline, "all-open", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(baseline, "all-closed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(baseline, "warm-all-open", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "État initial attendu : current, all-open, all-closed " +
                    "ou warm-all-open.");
            }
            if (string.Equals(baseline, "all-open", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(baseline, "all-closed", StringComparison.OrdinalIgnoreCase))
            {
                bool initiallyClosed = string.Equals(
                    baseline, "all-closed", StringComparison.OrdinalIgnoreCase);
                foreach (GameMepValveData currentValve in snapshot.Graph.Valves.Where(
                    item => item.IsEnabledAsValve &&
                        item.Kind == GameMepFlowControlKind.IsolationValve))
                {
                    currentValve.IsClosed = initiallyClosed;
                }
            }
            var engine = new GameMepSimulationEngine(snapshot.Graph);
            engine.Recalculate();
            if (string.Equals(
                    baseline, "warm-all-open", StringComparison.OrdinalIgnoreCase))
            {
                foreach (GameMepValveData closedValve in snapshot.Graph.Valves.Where(
                    item => item.IsEnabledAsValve &&
                        item.Kind == GameMepFlowControlKind.IsolationValve &&
                        item.IsClosed).ToArray())
                {
                    closedValve.IsClosed = false;
                    engine.Recalculate();
                }
            }

            var before = new Dictionary<Tuple<string, int>, GameMepReplayPathState>();
            foreach (GameMepElementData element in snapshot.Graph.Elements)
            {
                for (int ordinal = 0; ordinal < element.Paths.Count; ordinal++)
                {
                    GameMepPathData path = element.Paths[ordinal];
                    before[Tuple.Create(element.Key, ordinal)] =
                        new GameMepReplayPathState
                        {
                            ElementKey = element.Key,
                            PathOrdinal = ordinal,
                            FlowState = path.FlowState,
                            HasCirculation = path.HasCirculation,
                            FlowForward = path.FlowForward,
                            DirectionState = path.DirectionState,
                            DirectionReason = path.DirectionReason
                        };
                }
            }

            GameMepElementData? target = snapshot.Graph.FindElement(valveElementId);
            GameMepValveData? valve = target == null
                ? null
                : snapshot.Graph.FindValve(target.Key);
            if (target == null || valve == null)
                throw new InvalidOperationException("Vanne introuvable : " + valveElementId);

            valve.IsClosed = close;
            valve.WasManuallyOverridden = true;
            engine.Recalculate();

            int started = 0;
            int stopped = 0;
            int reversed = 0;
            var reversalReasons = new Dictionary<string, int>(StringComparer.Ordinal);
            Console.WriteLine(
                (close ? "Fermeture " : "Ouverture ") + valveElementId +
                " : " + target.Name);
            foreach (GameMepElementData element in snapshot.Graph.Elements)
            {
                for (int ordinal = 0; ordinal < element.Paths.Count; ordinal++)
                {
                    GameMepPathData path = element.Paths[ordinal];
                    if (!before.TryGetValue(
                            Tuple.Create(element.Key, ordinal),
                            out GameMepReplayPathState previous))
                    {
                        continue;
                    }

                    bool starts = !previous.HasCirculation && path.HasCirculation;
                    bool stops = previous.HasCirculation && !path.HasCirculation;
                    bool reverses = previous.HasCirculation && path.HasCirculation &&
                        previous.FlowForward != path.FlowForward;
                    if (!starts && !stops && !reverses)
                        continue;

                    if (starts)
                        started++;
                    if (stops)
                        stopped++;
                    if (reverses)
                    {
                        reversed++;
                        string reason = path.DirectionReason ?? string.Empty;
                        reversalReasons[reason] = reversalReasons.TryGetValue(
                            reason, out int count) ? count + 1 : 1;
                    }
                    Console.WriteLine(
                        "  " + element.ElementId + " " + element.Name +
                        " chemin " + ordinal + " : " +
                        (starts ? "démarre" : stops ? "s'arrête" : "s'inverse") +
                        ", avant=" + previous.DirectionReason +
                        ", après=" + path.DirectionReason);
                }
            }
            Console.WriteLine(
                "Bilan : " + started + " démarrages, " + stopped +
                " arrêts, " + reversed + " inversions.");
            foreach (KeyValuePair<string, int> reason in reversalReasons
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.Ordinal))
            {
                Console.WriteLine("  " + reason.Value + " x " + reason.Key);
            }
            return 0;
        }

        private static void TwoSmallReturnsMergeIntoLargeCollector()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("return-a-inlet", 1, source: true);
            fixture.AddElement("return-a", 2);
            fixture.AddElement("return-b-far", 2);
            fixture.AddElement("return-b-near", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("collector", 2);
            fixture.AddElement("declared-return", 1);

            fixture.Connect("return-a-inlet", 0, "return-a", 0);
            fixture.Connect("return-a", 1, "junction", 0);
            // Ce deuxième retour n'est pas déclaré comme arrivée : son bout
            // ouvert représente l'amont tronqué de la maquette. Sans la règle
            // de collecteur, il était pris à tort pour une sortie implicite.
            // Deux tronçons successifs sont volontairement dessinés dans des
            // ordres opposés. Toute la branche doit néanmoins converger sans
            // qu'une portion intermédiaire reparte à contre-sens.
            fixture.Connect("return-b-far", 1, "return-b-near", 1);
            fixture.Connect("return-b-near", 0, "junction", 1);
            fixture.Connect("junction", 2, "collector", 0);
            fixture.Connect("collector", 1, "declared-return", 0);
            fixture.AddBoundary("declared-return", GameMepBoundaryKind.Outlet);
            fixture.SetPortDiameter("junction", 0, 200.0);
            fixture.SetPortDiameter("junction", 1, 200.0);
            fixture.SetPortDiameter("junction", 2, 300.0);
            fixture.AddJunctionPaths("junction");
            fixture.SetPathLength("return-a", 6.0);
            fixture.SetPathLength("return-b-far", 5.0);
            fixture.SetPathLength("return-b-near", 5.0);
            fixture.SetPathLength("collector", 8.0);

            fixture.Calculate();

            Assert(fixture.Path("return-a").FlowForward &&
                fixture.Path("return-b-far").FlowForward &&
                !fixture.Path("return-b-near").FlowForward &&
                fixture.Path("collector").FlowForward,
                "Every segment of both DN 200 returns must merge continuously into the DN 300 collector.");
            Assert(fixture.JunctionPath("junction", 0).FlowForward &&
                fixture.JunctionPath("junction", 1).FlowForward &&
                !fixture.JunctionPath("junction", 2).FlowForward,
                "The two small legs must enter the junction and the large leg must leave it.");
            Assert(fixture.JunctionPath("junction", 0).HasCirculation &&
                fixture.JunctionPath("junction", 1).HasCirculation &&
                fixture.JunctionPath("junction", 2).HasCirculation,
                "Every leg of a collector merge must remain animated.");
            AssertRenderableFlow(fixture, "return-a");
            AssertRenderableFlow(fixture, "return-b-far");
            AssertRenderableFlow(fixture, "return-b-near");
            AssertRenderableFlow(fixture, "collector");
            Assert(fixture.Graph.DiameterAwareJunctionCount == 1 &&
                fixture.Graph.DiameterInferredInletCount == 1 &&
                fixture.Graph.DiameterDirectedPathCount >= 1,
                "The diagnostics must prove that the ambiguous DN 200 path was recalculated by DN.");
        }

        private static void ReducerAfterEqualTeeProvidesEffectiveCollectorDn()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("inlet", 1, source: true);
            fixture.AddElement("small-a", 2);
            fixture.AddElement("small-b", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("reducer", 2);
            fixture.AddElement("collector", 2);
            fixture.AddElement("outlet", 1);

            fixture.Connect("inlet", 0, "small-a", 0);
            fixture.Connect("small-a", 1, "junction", 0);
            fixture.Connect("small-b", 0, "junction", 1);
            fixture.Connect("junction", 2, "reducer", 0);
            fixture.Connect("reducer", 1, "collector", 0);
            fixture.Connect("collector", 1, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            // La famille du té ne publie volontairement aucune section. Les
            // trois DN doivent être récupérés depuis les canalisations réelles
            // de chaque bras, y compris derrière le réducteur.
            fixture.SetPortDiameter("reducer", 0, 200.0);
            fixture.SetPortDiameter("reducer", 1, 300.0);
            fixture.SetPipeDiameter("small-a", 200.0);
            fixture.SetPipeDiameter("small-b", 200.0);
            fixture.SetPipeDiameter("collector", 300.0);
            fixture.AddJunctionPaths("junction");
            fixture.SetPathLength("small-a", 6.0);
            fixture.SetPathLength("small-b", 6.0);
            fixture.SetPathLength("collector", 8.0);

            fixture.Calculate();

            Assert(fixture.Graph.DiameterAwareJunctionCount == 1 &&
                fixture.Graph.DiameterInferredInletCount == 1,
                "A reducer immediately after an equal-size tee must expose the effective DN 300 arm.");
            Assert(!fixture.Path("small-b").FlowForward,
                "The second DN 200 must still merge through an equal-size tee followed by a reducer.");
            AssertRenderableFlow(fixture, "small-a");
            AssertRenderableFlow(fixture, "small-b");
            AssertRenderableFlow(fixture, "collector");
        }

        private static void CollectorMergeSurvivesDownstreamTee()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("inlet", 1, source: true);
            fixture.AddElement("small-a", 2);
            fixture.AddElement("small-b", 2);
            fixture.AddElement("merge-tee", 3);
            fixture.AddElement("collector", 2);
            fixture.AddElement("downstream-tee", 3);
            fixture.AddElement("toward-return", 2);
            fixture.AddElement("side-leg", 2);
            fixture.AddElement("outlet", 1);

            fixture.Connect("inlet", 0, "small-a", 0);
            fixture.Connect("small-a", 1, "merge-tee", 0);
            fixture.Connect("small-b", 0, "merge-tee", 1);
            fixture.Connect("merge-tee", 2, "collector", 0);
            fixture.Connect("collector", 1, "downstream-tee", 0);
            fixture.Connect("downstream-tee", 1, "toward-return", 0);
            fixture.Connect("toward-return", 1, "outlet", 0);
            fixture.Connect("downstream-tee", 2, "side-leg", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.SetPortDiameter("merge-tee", 0, 200.0);
            fixture.SetPortDiameter("merge-tee", 1, 200.0);
            fixture.SetPortDiameter("merge-tee", 2, 300.0);
            fixture.SetPortDiameter("downstream-tee", 0, 300.0);
            fixture.SetPortDiameter("downstream-tee", 1, 300.0);
            fixture.SetPortDiameter("downstream-tee", 2, 300.0);
            fixture.SetPipeDiameter("small-a", 200.0);
            fixture.SetPipeDiameter("small-b", 200.0);
            fixture.SetPipeDiameter("collector", 300.0);
            fixture.SetPipeDiameter("toward-return", 300.0);
            fixture.SetPipeDiameter("side-leg", 300.0);
            fixture.AddJunctionPaths("merge-tee");
            fixture.AddJunctionPaths("downstream-tee");
            fixture.SetPathLength("small-a", 6.0);
            fixture.SetPathLength("small-b", 6.0);
            fixture.SetPathLength("collector", 8.0);

            fixture.Calculate();

            Assert(fixture.Graph.DiameterAwareJunctionCount == 1 &&
                fixture.Graph.DiameterInferredInletCount == 1,
                "A downstream tee on the DN 300 must be a local boundary, not cancel the upstream merge.");
            Assert(!fixture.Path("small-b").FlowForward &&
                fixture.Path("collector").FlowForward,
                "Both DN 200 legs must still converge into the DN 300 before the next tee.");
            AssertRenderableFlow(fixture, "small-a");
            AssertRenderableFlow(fixture, "small-b");
            AssertRenderableFlow(fixture, "collector");
        }

        private static void LargeSupplyStillDistributesToTwoSmallerBranches()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("supply", 1, source: true);
            fixture.AddElement("large-leg", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("branch-a", 2);
            fixture.AddElement("branch-b", 2);

            fixture.Connect("supply", 0, "large-leg", 0);
            fixture.Connect("large-leg", 1, "junction", 2);
            fixture.Connect("junction", 0, "branch-a", 0);
            fixture.Connect("junction", 1, "branch-b", 1);
            fixture.SetPortDiameter("junction", 0, 200.0);
            fixture.SetPortDiameter("junction", 1, 200.0);
            fixture.SetPortDiameter("junction", 2, 300.0);
            fixture.AddJunctionPaths("junction");
            fixture.SetPathLength("large-leg", 8.0);
            fixture.SetPathLength("branch-a", 6.0);
            fixture.SetPathLength("branch-b", 6.0);

            fixture.Calculate();

            Assert(fixture.Path("large-leg").FlowForward &&
                fixture.Path("branch-a").FlowForward &&
                !fixture.Path("branch-b").FlowForward,
                "A DN 300 supply must still split toward both DN 200 branches.");
            Assert(fixture.JunctionPath("junction", 2).FlowForward &&
                !fixture.JunctionPath("junction", 0).FlowForward &&
                !fixture.JunctionPath("junction", 1).FlowForward,
                "The collector heuristic must not turn a genuine distribution tee into a merge.");
            Assert(fixture.Graph.DiameterInferredInletCount == 0,
                "A genuine distribution must not infer any extra inlet.");
            Assert(fixture.Graph.DiameterAwareJunctionCount == 0 &&
                fixture.Graph.DiameterDirectedPathCount == 0 &&
                new[] { "large-leg", "branch-a", "branch-b" }.All(key =>
                    fixture.Path(key).DirectionReason.IndexOf(
                        "jonction de DN", StringComparison.OrdinalIgnoreCase) < 0),
                "A genuine large-to-small distribution must stay outside the DN correction scope.");
            AssertRenderableFlow(fixture, "large-leg");
            AssertRenderableFlow(fixture, "branch-a");
            AssertRenderableFlow(fixture, "branch-b");
        }

        private static void PumpOutletAnchorsLocalDnMerge()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("pump", 2);
            fixture.AddElement("small-a", 2);
            fixture.AddElement("small-b", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("collector", 2);
            fixture.AddElement("outlet", 1);

            fixture.Connect("source", 0, "pump", 0);
            fixture.Connect("pump", 1, "small-a", 0);
            fixture.Connect("small-a", 1, "junction", 0);
            fixture.Connect("small-b", 0, "junction", 1);
            fixture.Connect("junction", 2, "collector", 0);
            fixture.Connect("collector", 1, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.SetDirectionConstraint(
                "pump", 0, 1,
                GameMepDirectionConstraintScope.EquipmentPressureRise);
            fixture.SetPortDiameter("junction", 0, 200.0);
            fixture.SetPortDiameter("junction", 1, 200.0);
            fixture.SetPortDiameter("junction", 2, 300.0);
            fixture.AddJunctionPaths("junction");
            fixture.SetPathLength("small-a", 6.0);
            fixture.SetPathLength("small-b", 6.0);
            fixture.SetPathLength("collector", 8.0);

            fixture.Calculate();

            Assert(fixture.Path("pump").FlowForward &&
                fixture.Path("small-a").FlowForward &&
                !fixture.Path("small-b").FlowForward &&
                fixture.Path("collector").FlowForward,
                "A pump discharge on DN 200 must anchor both small returns toward the DN 300 collector.");
            Assert(fixture.Graph.DiameterAwareJunctionCount == 1 &&
                fixture.Graph.DiameterInferredInletCount == 1,
                "A pump outlet must be accepted as a local merge inlet without becoming a global source.");
            AssertRenderableFlow(fixture, "small-a");
            AssertRenderableFlow(fixture, "small-b");
            AssertRenderableFlow(fixture, "collector");
        }

        private static void NativePumpPortsImposeAspirationAndDischarge()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("suction", 2);
            fixture.AddElement("pump", 2);
            fixture.AddElement("discharge", 2);
            fixture.AddElement("outlet", 1);
            fixture.Connect("source", 0, "suction", 0);
            fixture.Connect("suction", 1, "pump", 0);
            fixture.Connect("pump", 1, "discharge", 0);
            fixture.Connect("discharge", 1, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.SetElementIdentity("pump", "Pompe primaire", "Pompe primaire");
            fixture.SetConnectorFlowDirection("pump", 0, "In");
            fixture.SetConnectorFlowDirection("pump", 1, "Out");
            fixture.SetPathLength("suction", 5.0);
            fixture.SetPathLength("pump", 2.0);
            fixture.SetPathLength("discharge", 5.0);

            fixture.Calculate();

            Assert(fixture.Path("pump").FlowForward &&
                fixture.Path("pump").DirectionReason.IndexOf(
                    "Sens natif de la pompe", StringComparison.Ordinal) >= 0,
                "Native Revit In/Out pump ports must impose aspiration then discharge. " +
                "Actual: forward=" + fixture.Path("pump").FlowForward +
                ", reason=" + fixture.Path("pump").DirectionReason);
            Assert(fixture.Path("suction").FlowForward &&
                fixture.Path("discharge").FlowForward,
                "The pipe before a native pump must be aspirated and the pipe after it discharged.");
        }

        private static void NativePumpSuctionOverridesSmallDnTeeInference()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("header-a", 2);
            fixture.AddElement("header-b", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("suction", 2);
            fixture.AddElement("pump", 2);
            fixture.AddElement("discharge", 2);
            fixture.AddElement("outlet", 1);
            fixture.Connect("source", 0, "header-a", 0);
            fixture.Connect("header-a", 1, "junction", 0);
            fixture.Connect("header-b", 0, "junction", 1);
            fixture.Connect("junction", 2, "suction", 0);
            fixture.Connect("suction", 1, "pump", 0);
            fixture.Connect("pump", 1, "discharge", 0);
            fixture.Connect("discharge", 1, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.SetElementIdentity("pump", "Pompe primaire", "Pompe primaire");
            fixture.SetConnectorFlowDirection("pump", 0, "In");
            fixture.SetConnectorFlowDirection("pump", 1, "Out");
            fixture.SetElementClassification("junction", "ReturnHydronic");
            fixture.SetPortDiameter("junction", 0, 300.0);
            fixture.SetPortDiameter("junction", 1, 300.0);
            fixture.SetPortDiameter("junction", 2, 200.0);
            fixture.AddJunctionPaths("junction");
            foreach (string key in new[]
            {
                "header-a", "header-b", "suction", "pump", "discharge"
            })
            {
                fixture.SetPathLength(key, 5.0);
            }

            fixture.Calculate();

            Assert(!fixture.JunctionPath("junction", 2).FlowForward &&
                fixture.Path("suction").FlowForward &&
                fixture.Path("pump").FlowForward,
                "A ReturnHydronic tee before a pump must flow from its center toward the native In port.");
        }

        private static void NativePumpSuctionCrossesPassiveChainToFirstTee()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("header", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("suction", 2);
            fixture.AddElement("flange", 2);
            fixture.AddElement("pump", 2);
            fixture.AddElement("discharge", 2);
            fixture.AddElement("outlet", 1);
            fixture.Connect("source", 0, "header", 0);
            fixture.Connect("header", 1, "junction", 0);
            fixture.Connect("junction", 2, "suction", 1);
            fixture.Connect("suction", 0, "flange", 1);
            fixture.Connect("flange", 0, "pump", 0);
            fixture.Connect("pump", 1, "discharge", 0);
            fixture.Connect("discharge", 1, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.SetElementIdentity("pump", "Pompe primaire", "Pompe primaire");
            fixture.SetConnectorFlowDirection("pump", 0, "In");
            fixture.SetConnectorFlowDirection("pump", 1, "Out");
            fixture.SetPipeDiameter("suction", 200.0);
            fixture.SetElementCategory(
                "flange", "Accessoire de canalisation");
            fixture.AddJunctionPaths("junction");
            foreach (string key in new[]
            {
                "header", "suction", "flange", "pump", "discharge"
            })
            {
                fixture.SetPathLength(key, 5.0);
            }

            fixture.Calculate();

            Assert(!fixture.Path("suction").FlowForward &&
                !fixture.Path("flange").FlowForward &&
                !fixture.JunctionPath("junction", 2).FlowForward,
                "Native pump suction must cross pipes and passive accessories up to the first tee.");
            Assert(fixture.Path("suction").DirectionReason.IndexOf(
                    "Aspiration propagée", StringComparison.Ordinal) >= 0,
                "The suction pipe must explain that its direction comes from the native pump inlet.");
        }

        private static void ParallelNativePumpDischargesDoNotReverseEachOther()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source-a", 1, source: true);
            fixture.AddElement("pump-a", 2);
            fixture.AddElement("discharge-a", 2);
            fixture.AddElement("source-b", 1, source: true);
            fixture.AddElement("pump-b", 2);
            fixture.AddElement("discharge-b", 2);
            fixture.AddElement("junction-a", 3);
            fixture.AddElement("collector", 2);
            fixture.AddElement("source-c", 1, source: true);
            fixture.AddElement("pump-c", 2);
            fixture.AddElement("discharge-c", 2);
            fixture.AddElement("junction-b", 3);
            fixture.AddElement("outlet-pipe", 2);
            fixture.AddElement("outlet", 1);

            fixture.Connect("source-a", 0, "pump-a", 0);
            fixture.Connect("pump-a", 1, "discharge-a", 0);
            fixture.Connect("discharge-a", 1, "junction-a", 0);
            fixture.Connect("source-b", 0, "pump-b", 0);
            fixture.Connect("pump-b", 1, "discharge-b", 0);
            fixture.Connect("discharge-b", 1, "junction-a", 1);
            fixture.Connect("junction-a", 2, "collector", 0);
            fixture.Connect("collector", 1, "junction-b", 0);
            fixture.Connect("source-c", 0, "pump-c", 0);
            fixture.Connect("pump-c", 1, "discharge-c", 0);
            fixture.Connect("discharge-c", 1, "junction-b", 1);
            fixture.Connect("junction-b", 2, "outlet-pipe", 0);
            fixture.Connect("outlet-pipe", 1, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);

            foreach (string pump in new[] { "pump-a", "pump-b", "pump-c" })
            {
                fixture.SetElementIdentity(pump, "Pompe réseaux", "Pompe réseaux");
                fixture.SetConnectorFlowDirection(pump, 0, "In");
                fixture.SetConnectorFlowDirection(pump, 1, "Out");
                fixture.SetPathLength(pump, 2.0);
            }
            foreach (string path in new[]
            {
                "discharge-a", "discharge-b", "collector",
                "discharge-c", "outlet-pipe"
            })
            {
                fixture.SetPathLength(path, 5.0);
            }
            fixture.AddJunctionPaths("junction-a");
            fixture.AddJunctionPaths("junction-b");
            fixture.SetElementClassification("junction-a", "ReturnHydronic");
            fixture.SetElementClassification("junction-b", "ReturnHydronic");
            fixture.SetPortDirection("junction-a", 0, -1.0, 0.0, 0.0);
            fixture.SetPortDirection("junction-a", 1, 0.0, 1.0, 0.0);
            fixture.SetPortDirection("junction-a", 2, 1.0, 0.0, 0.0);
            fixture.SetPortDirection("junction-b", 0, -1.0, 0.0, 0.0);
            fixture.SetPortDirection("junction-b", 1, 0.0, 1.0, 0.0);
            fixture.SetPortDirection("junction-b", 2, 1.0, 0.0, 0.0);

            fixture.Calculate();

            foreach (string discharge in new[]
            {
                "discharge-a", "discharge-b", "discharge-c"
            })
            {
                Assert(fixture.Path(discharge).FlowForward,
                    "Each native pump discharge must keep flowing away from its own Out port: " +
                    discharge);
                AssertRenderableFlow(fixture, discharge);
            }
            Assert(fixture.Path("collector").FlowForward &&
                fixture.Path("outlet-pipe").FlowForward,
                "Parallel pump branches must still merge toward the declared outlet.");
            Assert(fixture.Path("collector").DirectionReason.IndexOf(
                    "Consensus", StringComparison.OrdinalIgnoreCase) < 0,
                "A downstream pump must not vote backward through an upstream " +
                "collector; the arrival-to-return gradient remains authoritative.");
        }

        private static void ConflictingPumpVotesDoNotUsePumpCountAsFlowRate()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("left-source", 1, source: true);
            fixture.AddElement("left-pump", 2);
            fixture.AddElement("left-junction", 3);
            fixture.AddElement("left-open-end", 1, source: true);
            fixture.AddElement("shared-header", 2);
            fixture.AddElement("right-junction", 4);
            fixture.AddElement("right-source-a", 1, source: true);
            fixture.AddElement("right-pump-a", 2);
            fixture.AddElement("right-source-b", 1, source: true);
            fixture.AddElement("right-pump-b", 2);
            fixture.AddElement("outlet-pipe", 2);
            fixture.AddElement("outlet", 1);

            fixture.Connect("left-source", 0, "left-pump", 0);
            fixture.Connect("left-pump", 1, "left-junction", 0);
            fixture.Connect("left-junction", 1, "shared-header", 0);
            fixture.Connect("left-junction", 2, "left-open-end", 0);
            fixture.Connect("shared-header", 1, "right-junction", 0);
            fixture.Connect("right-source-a", 0, "right-pump-a", 0);
            fixture.Connect("right-pump-a", 1, "right-junction", 1);
            fixture.Connect("right-source-b", 0, "right-pump-b", 0);
            fixture.Connect("right-pump-b", 1, "right-junction", 2);
            fixture.Connect("right-junction", 3, "outlet-pipe", 0);
            fixture.Connect("outlet-pipe", 1, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.AddJunctionPaths("left-junction");
            fixture.AddJunctionPaths("right-junction");

            foreach (string pump in new[]
            {
                "left-pump", "right-pump-a", "right-pump-b"
            })
            {
                fixture.SetElementIdentity(pump, "Pompe réseau", "Pompe réseau");
                fixture.SetConnectorFlowDirection(pump, 0, "In");
                fixture.SetConnectorFlowDirection(pump, 1, "Out");
                fixture.SetPathLength(pump, 2.0);
            }
            fixture.SetPathLength("shared-header", 8.0);
            fixture.SetPathLength("outlet-pipe", 6.0);

            fixture.Calculate();

            Assert(fixture.Path("shared-header").FlowForward,
                "Two opposing pump votes must not outweigh the actual " +
                "arrival-to-return direction on a shared header.");
            AssertRenderableFlow(fixture, "shared-header");
            Assert(fixture.Path("shared-header").DirectionReason.IndexOf(
                    "Consensus", StringComparison.OrdinalIgnoreCase) < 0,
                "A header receiving pump votes from both directions must keep " +
                "its hydraulic gradient instead of applying a pump majority.");
            Assert(fixture.Path("outlet-pipe").FlowForward,
                "Every pump branch must still merge toward the declared outlet.");
        }

        private static void OpeningValvePreservesEstablishedPumpConsensus()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("left-source", 1, source: true);
            fixture.AddElement("header-a", 2);
            fixture.AddElement("flange", 2);
            fixture.AddElement("header-b", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("pump-source", 1, source: true);
            fixture.AddElement("pump", 2);
            fixture.AddElement("outlet", 1);
            fixture.AddElement("state-valve", 2, valve: true);

            fixture.Connect("left-source", 0, "header-a", 0);
            fixture.Connect("header-a", 1, "flange", 0);
            fixture.Connect("flange", 1, "header-b", 0);
            fixture.Connect("header-b", 1, "junction", 0);
            fixture.Connect("pump-source", 0, "pump", 0);
            fixture.Connect("pump", 1, "junction", 1);
            fixture.Connect("junction", 2, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.AddJunctionPaths("junction");
            fixture.SetElementIdentity("pump", "Pompe réseau", "Pompe réseau");
            fixture.SetConnectorFlowDirection("pump", 0, "In");
            fixture.SetConnectorFlowDirection("pump", 1, "Out");
            fixture.SetElementCategory(
                "flange", "Accessoire de canalisation");
            foreach (string path in new[]
            {
                "header-a", "flange", "header-b", "pump"
            })
            {
                fixture.SetPathLength(path, 5.0);
            }
            fixture.CloseValve("state-valve");
            fixture.Graph.RebuildIndexes();
            var engine = new GameMepSimulationEngine(fixture.Graph);
            engine.Recalculate();

            // Représente le sens du collecteur déjà affiché avant l'ouverture.
            // La pompe branchée à droite vote dans l'autre sens, mais une
            // branche qui vient de s'ouvrir ne doit pas retourner l'existant.
            foreach (string header in new[] { "header-a", "header-b" })
            {
                GameMepPathData path = fixture.Path(header);
                path.FlowForward = true;
                path.HasCirculation = true;
                path.FlowState = GameMepFlowState.Supplied;
                path.DirectionState = GameMepDirectionState.Resolved;
                path.DirectionReason =
                    "Consensus des sens In/Out à travers les branches parallèles";
            }
            fixture.Path("flange").FlowForward = true;
            fixture.Path("flange").HasCirculation = true;
            fixture.Path("flange").FlowState = GameMepFlowState.Supplied;
            fixture.Path("flange").DirectionState =
                GameMepDirectionState.Resolved;
            fixture.Path("flange").DirectionReason =
                "Continuité avec les canalisations autour du composant à deux ports";

            fixture.Graph.FindValve("state-valve")!.IsClosed = false;
            engine.Recalculate();

            Assert(fixture.Path("header-a").FlowForward &&
                fixture.Path("header-b").FlowForward,
                "Opening a valve must not let a newly available pump branch " +
                "reverse an established circulating header.");
            Assert(fixture.Path("flange").FlowForward,
                "Passive accessories must be realigned after the stable pump " +
                "consensus is restored.");
        }

        private static void DirectionalEquipmentProtectsItsBranchFromAnotherSplit()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("pump-source", 1, source: true);
            fixture.AddElement("pump", 2);
            fixture.AddElement("pump-branch", 2);
            fixture.AddElement("equipment-source", 1, source: true);
            fixture.AddElement("directional-equipment", 2);
            fixture.AddElement("protected-branch", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("outlet-pipe", 2);
            fixture.AddElement("outlet", 1);

            fixture.Connect("pump-source", 0, "pump", 0);
            fixture.Connect("pump", 1, "pump-branch", 0);
            fixture.Connect("pump-branch", 1, "junction", 0);
            fixture.Connect("equipment-source", 0, "directional-equipment", 0);
            fixture.Connect("directional-equipment", 1, "protected-branch", 0);
            fixture.Connect("protected-branch", 1, "junction", 1);
            fixture.Connect("junction", 2, "outlet-pipe", 0);
            fixture.Connect("outlet-pipe", 1, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);

            fixture.SetElementIdentity("pump", "Pompe primaire", "Pompe primaire");
            fixture.SetConnectorFlowDirection("pump", 0, "In");
            fixture.SetConnectorFlowDirection("pump", 1, "Out");
            fixture.SetElementIdentity(
                "directional-equipment", "Équipement directionnel", "Équipement directionnel");
            fixture.SetConnectorFlowDirection("directional-equipment", 0, "In");
            fixture.SetConnectorFlowDirection("directional-equipment", 1, "Out");
            fixture.AddJunctionPaths("junction");
            foreach (string path in new[]
            {
                "pump", "pump-branch", "directional-equipment",
                "protected-branch", "outlet-pipe"
            })
            {
                fixture.SetPathLength(path, 5.0);
            }

            fixture.Calculate();

            Assert(fixture.Path("pump-branch").FlowForward,
                "The pump branch must enter the common junction.");
            Assert(fixture.Path("protected-branch").FlowForward,
                "A non-pump In/Out equipment branch must not be reversed by another split.");
            Assert(fixture.Path("directional-equipment").FlowForward &&
                fixture.Path("outlet-pipe").FlowForward,
                "Both independent inlets must continue toward the shared outlet.");
        }

        private static void PumpBranchesKeepTheirEstablishedDirection()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("supply", 1, source: true);
            fixture.AddElement("large-leg", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("left-branch", 2);
            fixture.AddElement("left-pump", 2);
            fixture.AddElement("left-after", 2);
            fixture.AddElement("left-outlet", 1);
            fixture.AddElement("right-branch", 2);
            fixture.AddElement("right-pump", 2);
            fixture.AddElement("right-after", 2);
            fixture.AddElement("right-outlet", 1);

            fixture.Connect("supply", 0, "large-leg", 0);
            fixture.Connect("large-leg", 1, "junction", 2);
            fixture.Connect("junction", 0, "left-branch", 0);
            fixture.Connect("left-branch", 1, "left-pump", 0);
            fixture.Connect("left-pump", 1, "left-after", 0);
            fixture.Connect("left-after", 1, "left-outlet", 0);
            fixture.Connect("junction", 1, "right-branch", 0);
            fixture.Connect("right-branch", 1, "right-pump", 0);
            fixture.Connect("right-pump", 1, "right-after", 0);
            fixture.Connect("right-after", 1, "right-outlet", 0);
            fixture.AddBoundary("left-outlet", GameMepBoundaryKind.Outlet);
            fixture.AddBoundary("right-outlet", GameMepBoundaryKind.Outlet);
            fixture.SetDirectionConstraint(
                "left-pump", 0, 1,
                GameMepDirectionConstraintScope.EquipmentPressureRise);
            fixture.SetDirectionConstraint(
                "right-pump", 0, 1,
                GameMepDirectionConstraintScope.EquipmentPressureRise);
            fixture.SetPortDiameter("junction", 0, 200.0);
            fixture.SetPortDiameter("junction", 1, 200.0);
            fixture.SetPortDiameter("junction", 2, 300.0);
            fixture.AddJunctionPaths("junction");
            foreach (string key in new[]
            {
                "large-leg", "left-branch", "left-after",
                "right-branch", "right-after"
            })
            {
                fixture.SetPathLength(key, 6.0);
            }

            fixture.Calculate();

            foreach (string key in new[]
            {
                "large-leg", "left-branch", "left-after",
                "right-branch", "right-after"
            })
            {
                Assert(fixture.Path(key).FlowForward,
                    "Both pump branches must keep one continuous downstream direction: " + key);
                AssertRenderableFlow(fixture, key);
            }
            Assert(fixture.Path("left-pump").FlowForward &&
                fixture.Path("right-pump").FlowForward,
                "Explicit pump directions must remain authoritative.");
            Assert(fixture.Graph.DiameterAwareJunctionCount == 0 &&
                fixture.Graph.DiameterDirectedPathCount == 0 &&
                fixture.Graph.DiameterInferredInletCount == 0,
                "A pump distribution must never activate the collector DN heuristic.");
        }

        private static void ExplicitSmallOutletPreventsMergeInference()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("inlet", 1, source: true);
            fixture.AddElement("small-a", 2);
            fixture.AddElement("small-b", 2);
            fixture.AddElement("small-outlet", 1);
            fixture.AddElement("junction", 3);
            fixture.AddElement("collector", 2);
            fixture.AddElement("large-outlet", 1);

            fixture.Connect("inlet", 0, "small-a", 0);
            fixture.Connect("small-a", 1, "junction", 0);
            fixture.Connect("junction", 1, "small-b", 0);
            fixture.Connect("small-b", 1, "small-outlet", 0);
            fixture.Connect("junction", 2, "collector", 0);
            fixture.Connect("collector", 1, "large-outlet", 0);
            fixture.AddBoundary("small-outlet", GameMepBoundaryKind.Outlet);
            fixture.AddBoundary("large-outlet", GameMepBoundaryKind.Outlet);
            fixture.SetPortDiameter("junction", 0, 200.0);
            fixture.SetPortDiameter("junction", 1, 200.0);
            fixture.SetPortDiameter("junction", 2, 300.0);
            fixture.AddJunctionPaths("junction");
            fixture.SetPathLength("small-a", 6.0);
            fixture.SetPathLength("small-b", 6.0);
            fixture.SetPathLength("collector", 8.0);

            fixture.Calculate();

            Assert(fixture.Graph.DiameterInferredInletCount == 0,
                "An explicit outlet on a small leg must always beat the DN merge heuristic.");
            Assert(fixture.Path("small-b").FlowForward,
                "The explicitly declared small outlet must keep receiving flow from the tee.");
            AssertRenderableFlow(fixture, "small-b");
        }

        private static void SubthresholdDnDoesNotInferMerge()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("inlet", 1, source: true);
            fixture.AddElement("small-a", 2);
            fixture.AddElement("small-b", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("collector", 2);
            fixture.AddElement("outlet", 1);

            fixture.Connect("inlet", 0, "small-a", 0);
            fixture.Connect("small-a", 1, "junction", 0);
            fixture.Connect("junction", 1, "small-b", 0);
            fixture.Connect("junction", 2, "collector", 0);
            fixture.Connect("collector", 1, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.SetPortDiameter("junction", 0, 250.0);
            fixture.SetPortDiameter("junction", 1, 250.0);
            fixture.SetPortDiameter("junction", 2, 300.0);
            fixture.AddJunctionPaths("junction");
            fixture.SetPathLength("small-a", 6.0);
            fixture.SetPathLength("small-b", 6.0);
            fixture.SetPathLength("collector", 8.0);

            fixture.Calculate();

            Assert(fixture.Graph.DiameterAwareJunctionCount == 0 &&
                fixture.Graph.DiameterInferredInletCount == 0,
                "A DN ratio below 1.25 must preserve the historical topology.");
            Assert(fixture.Path("small-a").FlowForward &&
                fixture.Path("small-b").FlowForward &&
                fixture.Path("collector").FlowForward,
                "Below the DN threshold, the historical split direction must remain unchanged.");
            AssertRenderableFlow(fixture, "small-a");
            Assert(fixture.Path("small-b").FlowState == GameMepFlowState.Supplied &&
                fixture.Path("small-b").HasCirculation,
                "Below the threshold, the historical implicit outlet must remain supplied and circulating.");
            AssertRenderableFlow(fixture, "collector");
        }

        private static void LocalOverrideDoesNotDisableNeighboringDnMerge()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("inlet", 1, source: true);
            fixture.AddElement("small-a", 2);
            fixture.AddElement("small-b", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("collector", 2);
            fixture.AddElement("outlet", 1);

            fixture.Connect("inlet", 0, "small-a", 0);
            fixture.Connect("small-a", 1, "junction", 0);
            fixture.Connect("small-b", 0, "junction", 1);
            fixture.Connect("junction", 2, "collector", 0);
            fixture.Connect("collector", 1, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.SetDirectionConstraint("small-a", 0, 1);
            fixture.SetPortDiameter("junction", 0, 200.0);
            fixture.SetPortDiameter("junction", 1, 200.0);
            fixture.SetPortDiameter("junction", 2, 300.0);
            fixture.AddJunctionPaths("junction");
            fixture.SetPathLength("small-a", 6.0);
            fixture.SetPathLength("small-b", 6.0);
            fixture.SetPathLength("collector", 8.0);

            fixture.Calculate();

            Assert(fixture.Path("small-a").FlowForward &&
                fixture.Path("small-a").DirectionReason.IndexOf(
                    "Correction locale", StringComparison.OrdinalIgnoreCase) >= 0,
                "The local override must keep priority on its own DN 200 branch.");
            Assert(fixture.Graph.DiameterInferredInletCount == 1 &&
                !fixture.Path("small-b").FlowForward,
                "A local override must not disable merge inference on the neighboring DN 200.");
            AssertRenderableFlow(fixture, "small-a");
            AssertRenderableFlow(fixture, "small-b");
            AssertRenderableFlow(fixture, "collector");
        }

        private static void DnMergeInferenceSkipsRejoinedBypass()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("inlet", 1, source: true);
            fixture.AddElement("small-a", 2);
            fixture.AddElement("bypass", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("collector", 2);
            fixture.AddElement("outlet", 1);

            fixture.Connect("inlet", 0, "small-a", 0);
            fixture.Connect("small-a", 1, "junction", 0);
            fixture.Connect("junction", 1, "bypass", 0);
            fixture.Connect("bypass", 1, "collector", 0);
            fixture.Connect("junction", 2, "collector", 0);
            fixture.Connect("collector", 1, "outlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.SetPortDiameter("junction", 0, 200.0);
            fixture.SetPortDiameter("junction", 1, 200.0);
            fixture.SetPortDiameter("junction", 2, 300.0);
            fixture.SetPipeDiameter("small-a", 200.0);
            fixture.SetPipeDiameter("bypass", 200.0);
            fixture.SetPipeDiameter("collector", 300.0);
            fixture.AddJunctionPaths("junction");
            fixture.SetPathLength("small-a", 6.0);
            fixture.SetPathLength("bypass", 6.0);
            fixture.SetPathLength("collector", 8.0);

            fixture.Calculate();

            Assert(fixture.Graph.DiameterAwareJunctionCount == 0 &&
                fixture.Graph.DiameterInferredInletCount == 0 &&
                fixture.Graph.DiameterDirectedPathCount == 0,
                "Two tee arms rejoined outside the fitting are a bypass and must not activate the DN rule.");
            Assert(new[] { "small-a", "bypass", "collector" }.All(key =>
                    fixture.Path(key).DirectionReason.IndexOf(
                        "jonction de DN", StringComparison.OrdinalIgnoreCase) < 0),
                "A rejected bypass must preserve the historical pipe directions.");
        }

        private static void BalancedBypassAtOneNodeRemainsStagnant()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("node", 1);
            fixture.AddElement("bypass", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("arrival", 0, "node", 0);
            fixture.Connect("return", 0, "node", 0);
            fixture.Connect("bypass", 0, "node", 0);
            fixture.Connect("bypass", 1, "node", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);

            fixture.Calculate();

            AssertState(fixture, "bypass", GameMepFlowState.Supplied);
            Assert(!fixture.Path("bypass").HasCirculation,
                "A bypass tied to the same hydraulic node must remain stagnant.");
        }

        private static void HydraulicPotentialEliminatesIsolatedLoopReversal()
        {
            GraphFixture fixture = new GraphFixture();
            for (int index = 0; index < 7; index++)
                fixture.AddElement("node-" + index, 1, source: index == 0);

            AddPipe("pipe-01", 0, 1);
            AddPipe("pipe-04", 0, 4);
            AddPipe("pipe-06", 0, 6);
            AddPipe("pipe-12", 1, 2);
            AddPipe("pipe-15", 1, 5);
            AddPipe("pipe-23", 2, 3);
            AddPipe("pipe-35", 3, 5);
            AddPipe("pipe-46", 4, 6);
            AddPipe("pipe-45", 4, 5);
            fixture.AddBoundary("node-1", GameMepBoundaryKind.Outlet);
            fixture.AddBoundary("node-6", GameMepBoundaryKind.Outlet);

            fixture.Calculate();

            Assert(fixture.Path("pipe-35").HasCirculation &&
                !fixture.Path("pipe-35").FlowForward,
                "A loop segment must follow the final hydraulic potential instead of reversing locally because of nearest-boundary distances.");
            AssertRenderableFlow(fixture, "pipe-35");

            void AddPipe(string key, int startNode, int endNode)
            {
                fixture.AddElement(key, 2);
                fixture.Connect("node-" + startNode, 0, key, 0);
                fixture.Connect(key, 1, "node-" + endNode, 0);
                fixture.SetPathLength(key, 6.0);
            }
        }

        private static void TeePortsFollowTheirAdjacentPipes()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("before", 2);
            fixture.AddElement("tee", 3);
            fixture.AddElement("after-1", 2);
            fixture.AddElement("after-2", 2);
            fixture.AddElement("after-3", 2);
            fixture.AddElement("return", 1);
            fixture.AddElement("short-side-outlet", 1);

            fixture.Connect("source", 0, "before", 0);
            fixture.Connect("before", 1, "tee", 0);
            fixture.Connect("tee", 1, "after-1", 0);
            fixture.Connect("after-1", 1, "after-2", 0);
            fixture.Connect("after-2", 1, "after-3", 0);
            fixture.Connect("after-3", 1, "return", 0);
            fixture.Connect("tee", 2, "short-side-outlet", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);
            fixture.SetPortDirection("tee", 0, 0.0, -1.0, 0.0);
            fixture.SetPortDirection("tee", 1, 0.0, 1.0, 0.0);
            fixture.SetPortDirection("tee", 2, 1.0, 0.0, 0.0);
            fixture.AddJunctionPaths("tee");

            fixture.Calculate();

            Assert(fixture.Path("before").FlowForward &&
                fixture.Path("after-1").FlowForward &&
                fixture.Path("after-2").FlowForward &&
                fixture.Path("after-3").FlowForward &&
                fixture.JunctionPath("tee", 0).FlowForward &&
                !fixture.JunctionPath("tee", 1).FlowForward &&
                fixture.JunctionPath("tee", 1).DirectionReason.IndexOf(
                    "géométrique", StringComparison.OrdinalIgnoreCase) >= 0,
                "Each main tee port must keep the same local flow direction as its adjacent pipe, even when a short side outlet skews the virtual-center potential.");
        }

        private static void CollinearTeeChainKeepsOneHeaderDirection()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("before", 2);
            fixture.AddElement("lower-tee", 3);
            fixture.AddElement("between-tees", 2);
            fixture.AddElement("upper-tee", 3);
            fixture.AddElement("after", 2);
            fixture.AddElement("return", 1);
            fixture.AddElement("lower-side", 1);
            fixture.AddElement("upper-side", 1);

            fixture.Connect("source", 0, "before", 0);
            fixture.Connect("before", 1, "lower-tee", 0);
            fixture.Connect("lower-tee", 1, "between-tees", 1);
            fixture.Connect("between-tees", 0, "upper-tee", 0);
            fixture.Connect("upper-tee", 1, "after", 0);
            fixture.Connect("after", 1, "return", 0);
            fixture.Connect("lower-tee", 2, "lower-side", 0);
            fixture.Connect("upper-tee", 2, "upper-side", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);
            foreach (string tee in new[] { "lower-tee", "upper-tee" })
            {
                fixture.SetPortDirection(tee, 0, 0.0, -1.0, 0.0);
                fixture.SetPortDirection(tee, 1, 0.0, 1.0, 0.0);
                fixture.SetPortDirection(tee, 2, 1.0, 0.0, 0.0);
                fixture.AddJunctionPaths(tee);
            }

            fixture.Calculate();

            Assert(fixture.Path("before").FlowForward &&
                !fixture.Path("between-tees").FlowForward &&
                fixture.Path("after").FlowForward &&
                fixture.JunctionPath("lower-tee", 0).FlowForward &&
                !fixture.JunctionPath("lower-tee", 1).FlowForward &&
                fixture.JunctionPath("upper-tee", 0).FlowForward &&
                !fixture.JunctionPath("upper-tee", 1).FlowForward,
                "Two collinear tees must preserve one continuous header direction regardless of each pipe's Revit endpoint order.");
        }

        private static void TwoPortFittingFollowsAuthoritativeAdjacentPaths()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("outlet", 1);
            fixture.AddElement("left-pipe", 2);
            fixture.AddElement("fitting-a", 2);
            fixture.AddElement("fitting-b", 2);
            fixture.AddElement("right-pipe", 2);
            fixture.AddElement("inlet", 1, source: true);
            fixture.Connect("outlet", 0, "left-pipe", 0);
            fixture.Connect("left-pipe", 1, "fitting-a", 0);
            fixture.Connect("fitting-a", 1, "fitting-b", 0);
            fixture.Connect("fitting-b", 1, "right-pipe", 0);
            fixture.Connect("right-pipe", 1, "inlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.SetDirectionConstraint("left-pipe", 0, 1);
            fixture.SetDirectionConstraint("right-pipe", 0, 1);
            fixture.MarkPipeFitting("fitting-a");
            fixture.MarkPipeFitting("fitting-b");
            fixture.SetPathLength("left-pipe", 6.0);
            fixture.SetPathLength("fitting-a", 2.0);
            fixture.SetPathLength("fitting-b", 2.0);
            fixture.SetPathLength("right-pipe", 6.0);

            fixture.Calculate();

            Assert(fixture.Path("left-pipe").FlowForward &&
                fixture.Path("fitting-a").FlowForward &&
                fixture.Path("fitting-b").FlowForward &&
                fixture.Path("right-pipe").FlowForward,
                "A chain of two-port pipe fittings must follow agreeing authoritative neighboring paths.");
            AssertRenderableFlow(fixture, "fitting-a");
            AssertRenderableFlow(fixture, "fitting-b");
        }

        private static void TwoPortPipeAccessoryFollowsAuthoritativeAdjacentPaths()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("outlet", 1);
            fixture.AddElement("left-pipe", 2);
            fixture.AddElement("flange", 2);
            fixture.AddElement("open-valve", 2, valve: true);
            fixture.AddElement("right-pipe", 2);
            fixture.AddElement("inlet", 1, source: true);
            fixture.Connect("outlet", 0, "left-pipe", 0);
            fixture.Connect("left-pipe", 1, "flange", 0);
            fixture.Connect("flange", 1, "open-valve", 0);
            fixture.Connect("open-valve", 1, "right-pipe", 0);
            fixture.Connect("right-pipe", 1, "inlet", 0);
            fixture.AddBoundary("outlet", GameMepBoundaryKind.Outlet);
            fixture.SetDirectionConstraint("left-pipe", 0, 1);
            fixture.SetDirectionConstraint("right-pipe", 0, 1);
            fixture.SetElementCategory("flange", "Accessoire de canalisation");
            fixture.SetElementCategory("open-valve", "Accessoire de canalisation");
            fixture.SetPathLength("left-pipe", 6.0);
            fixture.SetPathLength("flange", 1.0);
            fixture.SetPathLength("open-valve", 2.0);
            fixture.SetPathLength("right-pipe", 6.0);

            fixture.Calculate();

            Assert(fixture.Path("left-pipe").FlowForward &&
                fixture.Path("flange").FlowForward &&
                fixture.Path("open-valve").FlowForward &&
                fixture.Path("right-pipe").FlowForward,
                "Open two-port pipe accessories must not reverse an established pipe direction.");
            AssertRenderableFlow(fixture, "flange");
            AssertRenderableFlow(fixture, "open-valve");
        }

        private static void PassiveContinuityCannotCrossDirectedBoundaryStop()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("branch-inlet", 1, source: true);
            fixture.AddElement("branch-pipe", 2);
            fixture.AddElement("open-valve", 2, valve: true);
            fixture.AddElement("directed-inlet", 2, source: true);
            fixture.AddElement("main-pipe", 2);
            fixture.AddElement("declared-return", 1);

            fixture.Connect("branch-inlet", 0, "branch-pipe", 0);
            fixture.Connect("branch-pipe", 1, "open-valve", 0);
            fixture.Connect("open-valve", 1, "directed-inlet", 0);
            fixture.Connect("directed-inlet", 1, "main-pipe", 0);
            fixture.Connect("main-pipe", 1, "declared-return", 0);
            fixture.SetDirectedSource("directed-inlet", 0, 1);
            fixture.AddBoundary("declared-return", GameMepBoundaryKind.Outlet);
            fixture.SetElementCategory(
                "open-valve", "Accessoire de canalisation");
            fixture.SetPathLength("branch-pipe", 5.0);
            fixture.SetPathLength("open-valve", 1.0);
            fixture.SetPathLength("directed-inlet", 2.0);
            fixture.SetPathLength("main-pipe", 5.0);

            fixture.Calculate();

            AssertRenderableFlow(fixture, "directed-inlet");
            AssertRenderableFlow(fixture, "main-pipe");
            AssertState(fixture, "branch-pipe", GameMepFlowState.Supplied);
            Assert(!fixture.Path("branch-pipe").HasCirculation &&
                !fixture.Path("open-valve").HasCirculation,
                "Passive continuity must not reactivate a supplied but hydraulically " +
                "excluded branch across an explicit boundary stop.");
        }

        private static void LoopBypassesClosedValve()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("valve", 2, valve: true);
            fixture.AddElement("bypass", 2);
            fixture.AddElement("target", 2);
            fixture.Connect("source", 0, "valve", 0);
            fixture.Connect("valve", 1, "target", 0);
            fixture.Connect("source", 0, "bypass", 0);
            fixture.Connect("bypass", 1, "target", 1);
            fixture.CloseValve("valve");
            fixture.Calculate();
            AssertState(fixture, "target", GameMepFlowState.Supplied);
            Assert(fixture.Graph.FindValve("valve")!.DownstreamState ==
                GameMepFlowState.Supplied,
                "A bypass must keep the downstream valve side supplied.");
        }

        private static void SecondSourceMaintainsSupply()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source-a", 1, source: true);
            fixture.AddElement("valve", 2, valve: true);
            fixture.AddElement("target", 2);
            fixture.AddElement("source-b", 1, source: true);
            fixture.Connect("source-a", 0, "valve", 0);
            fixture.Connect("valve", 1, "target", 0);
            fixture.Connect("source-b", 0, "target", 1);
            fixture.CloseValve("valve");
            fixture.Calculate();
            AssertState(fixture, "target", GameMepFlowState.Supplied);
        }

        private static void DisconnectedBranchIsIsolated()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("orphan", 2);
            fixture.Calculate();
            AssertState(fixture, "orphan", GameMepFlowState.Isolated);
        }

        private static void ThreeWayValveCutsEveryOutlet()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("valve", 3, valve: true);
            fixture.AddElement("a", 1);
            fixture.AddElement("b", 1);
            fixture.Connect("source", 0, "valve", 0);
            fixture.Connect("valve", 1, "a", 0);
            fixture.Connect("valve", 2, "b", 0);
            fixture.CloseValve("valve");
            fixture.Calculate();
            AssertState(fixture, "a", GameMepFlowState.Isolated);
            AssertState(fixture, "b", GameMepFlowState.Isolated);
        }

        private static void MissingDirectionsDoNotBreakReachability()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("undirected", 2);
            fixture.Connect("source", 0, "undirected", 0);
            fixture.Calculate();
            AssertState(fixture, "undirected", GameMepFlowState.Supplied);
            Assert(fixture.Graph.Connectors.All(connector => !connector.HasDirection),
                "The fixture must exercise connectors without direction metadata.");
        }

        private static void NetworkWithoutSourceStaysUnknown()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("pipe", 2);
            fixture.AddElement("terminal", 1);
            fixture.Connect("pipe", 1, "terminal", 0);
            fixture.Calculate();
            AssertState(fixture, "pipe", GameMepFlowState.Unknown);
            AssertState(fixture, "terminal", GameMepFlowState.Unknown);
        }

        private static void DirectedPipeSourceSuppliesOnlyChosenSide()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("upstream", 1);
            fixture.AddElement("boundary-pipe", 2);
            fixture.AddElement("downstream", 1);
            fixture.Connect("upstream", 0, "boundary-pipe", 0);
            fixture.Connect("boundary-pipe", 1, "downstream", 0);

            fixture.SetDirectedSource("boundary-pipe", 0, 1);
            fixture.Calculate();
            AssertState(fixture, "upstream", GameMepFlowState.Isolated);
            AssertState(fixture, "downstream", GameMepFlowState.Supplied);

            fixture.SetDirectedSource("boundary-pipe", 1, 0);
            fixture.Calculate();
            AssertState(fixture, "upstream", GameMepFlowState.Supplied);
            AssertState(fixture, "downstream", GameMepFlowState.Isolated);
        }

        private static void ArrivalAndReturnStabilizeDirection()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("pipe", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("arrival", 0, "pipe", 0);
            fixture.Connect("pipe", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);

            fixture.Calculate();

            GameMepPathData path = fixture.Path("pipe");
            Assert(path.DirectionState == GameMepDirectionState.Resolved,
                "An arrival and a return must resolve the direction between them.");
            Assert(path.FlowForward,
                "The flow must travel from the arrival-side connector to the return-side connector.");
            Assert(path.DirectionReason.IndexOf("retour", StringComparison.OrdinalIgnoreCase) >= 0,
                "The diagnostic must explain that the return helped resolve the direction.");
        }

        private static void OpenIsolationValveIsDirectionallyTransparent()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("before", 2);
            fixture.AddElement("open-valve", 2, valve: true);
            fixture.AddElement("after", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("arrival", 0, "before", 0);
            fixture.Connect("before", 1, "open-valve", 0);
            fixture.Connect("open-valve", 1, "after", 0);
            fixture.Connect("after", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);

            fixture.Calculate();

            Assert(fixture.Path("before").FlowForward &&
                fixture.Path("open-valve").FlowForward &&
                fixture.Path("after").FlowForward,
                "An open isolation valve must preserve the direction of its surrounding pipes.");
            Assert(fixture.Path("open-valve").HasCirculation,
                "An open isolation valve must display continuous circulation.");

            bool directionBeforeSecondCalculation =
                fixture.Path("open-valve").FlowForward;
            fixture.Calculate();
            Assert(fixture.Path("open-valve").FlowForward ==
                directionBeforeSecondCalculation,
                "Recalculation must never flip an open isolation valve.");
        }

        private static void SourceOnlyNetworkKeepsDirectionAcrossOpenValve()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("before", 2);
            fixture.AddElement("open-valve", 2, valve: true);
            fixture.AddElement("after", 2);
            fixture.Connect("arrival", 0, "before", 0);
            fixture.Connect("before", 1, "open-valve", 0);
            fixture.Connect("open-valve", 1, "after", 0);

            fixture.Calculate();

            Assert(fixture.Path("before").FlowForward &&
                fixture.Path("open-valve").FlowForward &&
                fixture.Path("after").FlowForward,
                "Implicit open ends must not turn a source-only network back towards its arrival.");
            Assert(new[] { "before", "open-valve", "after" }.All(key =>
                    fixture.Path(key).DirectionReason.IndexOf(
                        "potentiel entre", StringComparison.OrdinalIgnoreCase) < 0),
                "An implicit outlet must never replace the direction propagated from the source.");
        }

        private static void DifferentRevitSystemsDoNotExchangeDirection()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival-a", 1, source: true);
            fixture.AddElement("pipe-a", 2);
            fixture.AddElement("multi-system-equipment", 2);
            fixture.AddElement("pipe-b", 2);
            fixture.Connect("arrival-a", 0, "pipe-a", 0);
            fixture.Connect("pipe-a", 1, "multi-system-equipment", 0);
            fixture.Connect("multi-system-equipment", 1, "pipe-b", 0);
            fixture.SetElementSystem("arrival-a", "system-a");
            fixture.SetElementSystem("pipe-a", "system-a");
            fixture.SetConnectorSystem("multi-system-equipment", 0, "system-a");
            fixture.SetConnectorSystem("multi-system-equipment", 1, "system-b");
            fixture.SetElementSystem("pipe-b", "system-b");

            fixture.Calculate();

            AssertState(fixture, "pipe-a", GameMepFlowState.Supplied);
            AssertState(fixture, "pipe-b", GameMepFlowState.Unknown);
            Assert(fixture.Path("pipe-a").FlowForward,
                "System A must keep the direction propagated by its own source.");
        }

        private static void PumpConstraintBridgesDifferentRevitSystems()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival-a", 1, source: true);
            fixture.AddElement("pipe-a", 2);
            fixture.AddElement("pump", 2);
            fixture.AddElement("pipe-b", 2);
            fixture.Connect("arrival-a", 0, "pipe-a", 0);
            fixture.Connect("pipe-a", 1, "pump", 0);
            fixture.Connect("pump", 1, "pipe-b", 0);
            fixture.SetElementSystem("arrival-a", "system-a");
            fixture.SetElementSystem("pipe-a", "system-a");
            fixture.SetConnectorSystem("pump", 0, "system-a");
            fixture.SetConnectorSystem("pump", 1, "system-b");
            fixture.SetElementSystem("pipe-b", "system-b");
            fixture.SetDirectionConstraint(
                "pump",
                0,
                1,
                GameMepDirectionConstraintScope.EquipmentPressureRise);

            fixture.Calculate();

            AssertState(fixture, "pipe-a", GameMepFlowState.Supplied);
            AssertState(fixture, "pump", GameMepFlowState.Supplied);
            AssertState(fixture, "pipe-b", GameMepFlowState.Supplied);
            Assert(fixture.Path("pump").FlowForward,
                "A declared pump must bridge its two Revit system instances.");
        }

        private static void SharedSystemAbbreviationBridgesInlineEquipment()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddSystem("system-a", "PRI.R", "HydronicReturn");
            fixture.AddSystem("system-b", "PRI.R", "HydronicReturn");
            fixture.AddElement("arrival-a", 1, source: true);
            fixture.AddElement("pipe-a", 2);
            fixture.AddElement("inline-equipment", 2);
            fixture.AddElement("pipe-b", 2);
            fixture.Connect("arrival-a", 0, "pipe-a", 0);
            fixture.Connect("pipe-a", 1, "inline-equipment", 0);
            fixture.Connect("inline-equipment", 1, "pipe-b", 0);
            fixture.SetElementSystem("arrival-a", "system-a");
            fixture.SetElementSystem("pipe-a", "system-a");
            fixture.SetConnectorSystem("inline-equipment", 0, "system-a");
            fixture.SetConnectorSystem("inline-equipment", 1, "system-b");
            fixture.SetElementSystem("pipe-b", "system-b");

            fixture.Calculate();

            AssertState(fixture, "pipe-b", GameMepFlowState.Supplied);
        }

        private static void SharedSystemAbbreviationDoesNotBridgeMultiPortEquipment()
        {
            GraphFixture fixture = new GraphFixture();
            foreach (string key in new[]
                { "system-a", "system-b", "system-c", "system-d" })
            {
                fixture.AddSystem(key, "PRI.R", "HydronicReturn");
            }
            fixture.AddElement("arrival-a", 1, source: true);
            fixture.AddElement("pipe-a", 2);
            fixture.AddElement("heat-exchanger", 4);
            fixture.AddElement("pipe-b", 2);
            fixture.Connect("arrival-a", 0, "pipe-a", 0);
            fixture.Connect("pipe-a", 1, "heat-exchanger", 0);
            fixture.Connect("heat-exchanger", 1, "pipe-b", 0);
            fixture.SetElementSystem("arrival-a", "system-a");
            fixture.SetElementSystem("pipe-a", "system-a");
            fixture.SetConnectorSystem("heat-exchanger", 0, "system-a");
            fixture.SetConnectorSystem("heat-exchanger", 1, "system-b");
            fixture.SetConnectorSystem("heat-exchanger", 2, "system-c");
            fixture.SetConnectorSystem("heat-exchanger", 3, "system-d");
            fixture.SetElementSystem("pipe-b", "system-b");

            fixture.Calculate();

            AssertState(fixture, "pipe-b", GameMepFlowState.Unknown);
        }

        private static void PipeJunctionBridgesPhysicallyConnectedRevitSystems()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddSystem("cold-system", "EFS", "DomesticColdWater");
            fixture.AddSystem("process-system", "PROC", "OtherPipe");
            fixture.AddSystem("return-system", "RET", "HydronicReturn");
            fixture.AddElement("cold-inlet", 1, source: true);
            fixture.AddElement("cold-branch", 2);
            fixture.AddElement("junction", 3);
            fixture.AddElement("collector", 2);
            fixture.AddElement("declared-return", 1);

            fixture.Connect("cold-inlet", 0, "cold-branch", 0);
            fixture.Connect("cold-branch", 1, "junction", 0);
            fixture.Connect("junction", 2, "collector", 0);
            fixture.Connect("collector", 1, "declared-return", 0);
            fixture.SetElementSystem("cold-inlet", "cold-system");
            fixture.SetElementSystem("cold-branch", "cold-system");
            fixture.SetConnectorSystem("junction", 0, "cold-system");
            fixture.SetConnectorSystem("junction", 1, "process-system");
            fixture.SetConnectorSystem("junction", 2, "return-system");
            fixture.SetElementSystem("collector", "return-system");
            fixture.SetElementSystem("declared-return", "return-system");
            fixture.AddBoundary("declared-return", GameMepBoundaryKind.Outlet);
            fixture.SetPortDiameter("junction", 0, 80.0);
            fixture.SetPortDiameter("junction", 1, 80.0);
            fixture.SetPortDiameter("junction", 2, 300.0);
            fixture.AddJunctionPaths("junction");
            fixture.SetPathLength("cold-branch", 6.0);
            fixture.SetPathLength("collector", 8.0);

            fixture.Calculate();

            AssertState(fixture, "collector", GameMepFlowState.Supplied);
            AssertRenderableFlow(fixture, "cold-branch");
            AssertRenderableFlow(fixture, "collector");
        }

        private static void ReturnOnlySystemFlowsTowardDeclaredOutlet()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("far-return", 2);
            fixture.AddElement("return-valve", 2, valve: true);
            fixture.AddElement("near-return", 2);
            fixture.AddElement("declared-return", 1);
            fixture.Connect("far-return", 1, "return-valve", 0);
            fixture.Connect("return-valve", 1, "near-return", 0);
            fixture.Connect("near-return", 1, "declared-return", 0);
            foreach (string key in new[]
                { "far-return", "return-valve", "near-return", "declared-return" })
            {
                fixture.SetElementSystem(key, "return-system");
            }
            fixture.AddBoundary("declared-return", GameMepBoundaryKind.Outlet);

            fixture.Calculate();

            AssertState(fixture, "far-return", GameMepFlowState.Supplied);
            Assert(fixture.Path("far-return").FlowForward &&
                fixture.Path("return-valve").FlowForward &&
                fixture.Path("near-return").FlowForward,
                "A return-only system must point continuously toward its declared outlet.");
            Assert(fixture.Path("far-return").HasCirculation &&
                fixture.Path("near-return").HasCirculation,
                "A declared return must animate its own Revit system without an inlet.");

            fixture.CloseValve("return-valve");
            fixture.Calculate();

            AssertState(fixture, "far-return", GameMepFlowState.Isolated);
            AssertState(fixture, "near-return", GameMepFlowState.Supplied);
            Assert(!fixture.Path("far-return").HasCirculation &&
                !fixture.Path("return-valve").HasCirculation &&
                !fixture.Path("near-return").HasCirculation,
                "A closed valve must make both sides stagnant when it cuts the only return path.");
        }

        private static void ClosedValveCannotCreateImplicitReturnInlet()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("supply", 2);
            fixture.AddElement("valve", 2, valve: true);
            fixture.AddElement("return-tee", 3);
            fixture.AddElement("return-pipe", 2);
            fixture.AddElement("declared-return", 1);
            fixture.AddElement("open-return-leg", 2);

            fixture.Connect("arrival", 0, "supply", 0);
            fixture.Connect("supply", 1, "valve", 0);
            fixture.Connect("valve", 1, "return-tee", 0);
            fixture.Connect("return-tee", 1, "return-pipe", 0);
            fixture.Connect("return-pipe", 1, "declared-return", 0);
            fixture.Connect("return-tee", 2, "open-return-leg", 0);
            fixture.AddJunctionPaths("return-tee");
            fixture.AddBoundary("declared-return", GameMepBoundaryKind.Outlet);
            fixture.SetPathLength("supply", 5.0);
            fixture.SetPathLength("return-pipe", 5.0);
            fixture.SetPathLength("open-return-leg", 5.0);
            fixture.CloseValve("valve");

            fixture.Calculate();

            AssertState(fixture, "return-pipe", GameMepFlowState.Supplied);
            Assert(!fixture.Path("return-pipe").HasCirculation &&
                !fixture.Path("open-return-leg").HasCirculation &&
                fixture.Graph.FindElement("return-tee")!.Paths.All(path =>
                    !path.HasCirculation),
                "A return component isolated from an explicit inlet by a closed " +
                "valve must not turn its remaining open ends into hidden inlets.");
        }

        private static void ClosedValveDeadEndStaysPressurizedWithoutCirculation()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("main-before", 2);
            fixture.AddElement("tee", 3);
            fixture.AddElement("main-after", 2);
            fixture.AddElement("return", 1);
            fixture.AddElement("dead-leg", 2);
            fixture.AddElement("valve", 2, valve: true);
            fixture.AddElement("isolated-side", 1);

            fixture.Connect("arrival", 0, "main-before", 0);
            fixture.Connect("main-before", 1, "tee", 0);
            fixture.Connect("tee", 1, "main-after", 0);
            fixture.Connect("main-after", 1, "return", 0);
            fixture.Connect("tee", 2, "dead-leg", 0);
            fixture.Connect("dead-leg", 1, "valve", 0);
            fixture.Connect("valve", 1, "isolated-side", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);
            fixture.CloseValve("valve");

            fixture.Calculate();

            Assert(fixture.Path("main-before").HasCirculation &&
                fixture.Path("main-after").HasCirculation,
                "The arrival-to-return main route must keep moving fluid.");
            AssertState(fixture, "dead-leg", GameMepFlowState.Supplied);
            Assert(!fixture.Path("dead-leg").HasCirculation,
                "A branch ending at a closed valve must be pressurized but stagnant.");
            Assert(!fixture.Path("valve").HasCirculation,
                "No animated arrow may cross or originate from a closed valve.");
            AssertState(fixture, "isolated-side", GameMepFlowState.Isolated);
        }

        private static void UnmarkedOpenEndKeepsCirculation()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("tee", 3);
            fixture.AddElement("declared-return", 1);
            fixture.AddElement("unmarked-outlet", 2);

            fixture.Connect("arrival", 0, "tee", 0);
            fixture.Connect("tee", 1, "declared-return", 0);
            fixture.Connect("tee", 2, "unmarked-outlet", 0);
            fixture.AddBoundary("declared-return", GameMepBoundaryKind.Outlet);

            fixture.Calculate();

            AssertState(fixture, "unmarked-outlet", GameMepFlowState.Supplied);
            Assert(fixture.Path("unmarked-outlet").HasCirculation,
                "An open pipe end must behave like an implicit outlet until a closed valve blocks it.");
            Assert(fixture.Path("unmarked-outlet").FlowForward,
                "The implicit outlet must keep the visible flow directed away from the arrival.");
        }

        private static void EqualOpposingArrivalsStayAmbiguous()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival-a", 1, source: true);
            fixture.AddElement("pipe", 2);
            fixture.AddElement("arrival-b", 1, source: true);
            fixture.Connect("arrival-a", 0, "pipe", 0);
            fixture.Connect("arrival-b", 0, "pipe", 1);

            fixture.Calculate();
            GameMepPathData path = fixture.Path("pipe");
            Assert(path.DirectionState == GameMepDirectionState.Conflict,
                "Two equidistant opposing arrivals must be reported as ambiguous.");
            bool firstDirection = path.FlowForward;

            fixture.Graph.Sources.Reverse();
            fixture.Calculate();
            Assert(path.DirectionState == GameMepDirectionState.Conflict &&
                path.FlowForward == firstDirection,
                "Reordering sources must never flip an ambiguous arrow.");
        }

        private static void ParallelBypassesKeepTheSameDirection()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("bypass-a", 2);
            fixture.AddElement("bypass-b", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("arrival", 0, "bypass-a", 0);
            fixture.Connect("arrival", 0, "bypass-b", 0);
            fixture.Connect("bypass-a", 1, "return", 0);
            fixture.Connect("bypass-b", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);

            fixture.Calculate();

            Assert(fixture.Path("bypass-a").DirectionState == GameMepDirectionState.Resolved &&
                fixture.Path("bypass-a").FlowForward,
                "The first bypass must keep the arrival-to-return direction.");
            Assert(fixture.Path("bypass-b").DirectionState == GameMepDirectionState.Resolved &&
                fixture.Path("bypass-b").FlowForward,
                "The parallel bypass must keep the same arrival-to-return direction.");
            Assert(fixture.Path("bypass-a").HasCirculation &&
                fixture.Path("bypass-b").HasCirculation,
                "A real alternative route between an arrival and a return must keep circulating.");
        }

        private static void PumpConstraintDoesNotCreateSupply()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("pump", 2);
            fixture.SetDirectionConstraint(
                "pump",
                0,
                1,
                GameMepDirectionConstraintScope.EquipmentPressureRise);

            fixture.Calculate();

            AssertState(fixture, "pump", GameMepFlowState.Unknown);
            Assert(fixture.Path("pump").DirectionState == GameMepDirectionState.Resolved &&
                fixture.Path("pump").FlowForward,
                "A pump may impose direction without being treated as a fluid source.");
        }

        private static void ManualDirectionOverrideBeatsAutomaticPotential()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("pipe", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("arrival", 0, "pipe", 0);
            fixture.Connect("pipe", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);
            fixture.Calculate();
            Assert(fixture.Path("pipe").FlowForward,
                "The automatic potential must initially point towards the return.");

            fixture.SetDirectionConstraint("pipe", 1, 0);
            fixture.Calculate();

            Assert(fixture.Path("pipe").DirectionState == GameMepDirectionState.Resolved &&
                !fixture.Path("pipe").FlowForward,
                "A manual reversal must take precedence over the automatic potential.");
        }

        private static void LocalDirectionOverrideDoesNotReorientNeighbors()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("first", 2);
            fixture.AddElement("second", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("arrival", 0, "first", 0);
            fixture.Connect("first", 1, "second", 0);
            fixture.Connect("second", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);

            fixture.Calculate();
            Assert(fixture.Path("first").FlowForward &&
                fixture.Path("second").FlowForward,
                "The automatic direction must initially be continuous.");

            fixture.SetDirectionConstraint("first", 1, 0);
            fixture.Calculate();
            Assert(!fixture.Path("first").FlowForward,
                "The selected section must accept its local reversal.");
            Assert(fixture.Path("second").FlowForward,
                "A local reversal must never reorient the following section.");

            fixture.RemoveLocalDirectionConstraint("first");
            fixture.Calculate();
            Assert(fixture.Path("first").FlowForward &&
                fixture.Path("second").FlowForward,
                "Removing the local correction must restore automatic direction.");
        }

        private static void DirectionExplanationNamesSourceAndReturn()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("pipe", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("arrival", 0, "pipe", 0);
            fixture.Connect("pipe", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);

            fixture.Calculate();

            GameMepDirectionExplanationData explanation =
                fixture.Path("pipe").DirectionExplanation;
            Assert(explanation.PrimarySourceName == "arrival",
                "The explanation must identify the principal arrival.");
            Assert(explanation.InfluencingReturnName == "return",
                "The explanation must identify the return influencing direction.");
            Assert(explanation.UpstreamElementNames.First() == "arrival" &&
                explanation.Rule.IndexOf("retour", StringComparison.OrdinalIgnoreCase) >= 0,
                "The deterministic upstream path and applied rule must be exposed.");
        }

        private static void DirectionExplanationListsAlternativeSourcesDeterministically()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source-b", 1, source: true);
            fixture.AddElement("source-a", 1, source: true);
            fixture.AddElement("pipe", 2);
            fixture.Connect("source-a", 0, "pipe", 0);
            fixture.Connect("source-b", 0, "pipe", 0);

            fixture.Calculate();
            GameMepDirectionExplanationData first =
                fixture.Path("pipe").DirectionExplanation;
            string firstPrimary = first.PrimarySourceName;
            string firstAlternatives = string.Join("|", first.AlternativeSourceNames);

            fixture.Graph.Sources.Reverse();
            fixture.Graph.Connections.Reverse();
            fixture.Calculate();
            GameMepDirectionExplanationData second =
                fixture.Path("pipe").DirectionExplanation;

            Assert(firstPrimary == "source-a" &&
                second.PrimarySourceName == firstPrimary,
                "The principal source must be selected deterministically.");
            Assert(firstAlternatives == "source-b" &&
                string.Join("|", second.AlternativeSourceNames) == firstAlternatives,
                "Alternative sources must remain stable when internal collections are reordered.");
        }

        private static void PipeFittingCannotBecomeHydraulicSource()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("CML_Acier Té", 3, source: true);
            fixture.MarkPipeFitting("CML_Acier Té");
            fixture.AddJunctionPaths("CML_Acier Té");
            fixture.AddElement("pipe", 2);
            fixture.Connect("CML_Acier Té", 0, "pipe", 0);

            fixture.Calculate();

            Assert(fixture.Graph.FindElement("pipe")!.FlowState ==
                    GameMepFlowState.Unknown &&
                string.IsNullOrWhiteSpace(
                    fixture.Path("pipe").DirectionExplanation.PrimarySourceName),
                "A tee or other pipe fitting present in the source collection must be ignored by both the hydraulic calculation and its explanation.");
        }

        private static void DirectionExplanationUsesCurrentRevitElementIdentity()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("pipe", 2);
            fixture.Connect("source", 0, "pipe", 0);
            GameMepElementData sourceElement = fixture.Graph.FindElement("source")!;
            sourceElement.Name = "Arrivée chaufferie";
            sourceElement.ElementId = 4242;
            fixture.Graph.Sources.Single().Name = "CML_Acier Té";

            fixture.Calculate();

            Assert(fixture.Path("pipe").DirectionExplanation.PrimarySourceName ==
                    "Arrivée chaufferie (ID Revit 4242)",
                "A source explanation must use the current Revit element name and id, never a stale saved family name.");
        }

        private static void DirectionExplanationReportsAlternativeLoop()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("branch-a", 2);
            fixture.AddElement("branch-b", 2);
            fixture.AddElement("target", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("source", 0, "branch-a", 0);
            fixture.Connect("source", 0, "branch-b", 0);
            fixture.Connect("branch-a", 1, "target", 0);
            fixture.Connect("branch-b", 1, "target", 0);
            fixture.Connect("target", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);

            fixture.Calculate();

            Assert(fixture.Path("target").DirectionExplanation.HasAlternativeRoute,
                "A loop or bypass must be reported as an alternative route.");
        }

        private static void DirectionExplanationReportsClosedValveLimit()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("valve", 2, valve: true);
            fixture.AddElement("pipe", 2);
            fixture.Connect("source", 0, "valve", 0);
            fixture.Connect("valve", 1, "pipe", 0);
            fixture.CloseValve("valve");

            fixture.Calculate();

            Assert(fixture.Path("pipe").DirectionExplanation.LimitingControls
                .Any(item => item.IndexOf("fermée", StringComparison.OrdinalIgnoreCase) >= 0),
                "An isolated path must identify the neighboring closed valve.");
        }

        private static void DirectionExplanationMarksManualCorrection()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("pipe", 2);
            fixture.Connect("source", 0, "pipe", 0);
            fixture.SetDirectionConstraint("pipe", 1, 0);

            fixture.Calculate();

            GameMepDirectionExplanationData explanation =
                fixture.Path("pipe").DirectionExplanation;
            Assert(explanation.IsManual &&
                explanation.Reliability == GameMepDirectionReliability.Manual,
                "A local override must be clearly classified as manual.");
            Assert(explanation.Rule.IndexOf("manuelle", StringComparison.OrdinalIgnoreCase) >= 0,
                "The explanation must name the manual rule that won.");
        }

        private static void HistoryRestoresEveryScenarioMutation()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival", 1, source: true);
            fixture.AddElement("boundary", 2);
            fixture.AddElement("valve", 2, valve: true);
            fixture.AddElement("pump", 2);
            fixture.SetDirectedSource("boundary", 0, 1);
            GameMepSourceData arrival = fixture.Graph.Sources.First(item =>
                item.ElementKey == "arrival");
            GameMepSourceData boundary = fixture.Graph.Sources.First(item =>
                item.ElementKey == "boundary");
            GameMepValveData valve = fixture.Graph.FindValve("valve")!;
            var history = new GameMepScenarioHistory();

            Assert(history.Execute(fixture.Graph, "source inactive",
                () => { arrival.IsActive = false; arrival.WasManuallyOverridden = true; }),
                "A source activation change must be recorded.");
            Assert(history.Execute(fixture.Graph, "source reversed", () =>
            {
                int entry = boundary.EntryConnectorIndex;
                boundary.EntryConnectorIndex = boundary.ExitConnectorIndex;
                boundary.ExitConnectorIndex = entry;
            }), "A source direction change must be recorded.");
            Assert(history.Execute(fixture.Graph, "valve closed", () =>
            {
                valve.IsClosed = true;
                valve.WasManuallyOverridden = true;
            }), "A valve action must be recorded.");
            Assert(history.Execute(fixture.Graph, "local direction", () =>
                fixture.SetDirectionConstraint("boundary", 1, 0)),
                "A local direction correction must be recorded.");
            Assert(history.Execute(fixture.Graph, "pump constraint", () =>
                fixture.SetDirectionConstraint(
                    "pump", 0, 1,
                    GameMepDirectionConstraintScope.EquipmentPressureRise)),
                "A pump constraint must be recorded.");
            Assert(history.Execute(fixture.Graph, "source added", () =>
                fixture.Graph.Sources.Add(new GameMepSourceData
                {
                    ElementKey = "pump",
                    Name = "manual source",
                    IsActive = true,
                    IsUserCreated = true,
                    WasManuallyOverridden = true
                })), "Adding a manual source must be recorded.");

            Assert(history.UndoCount == 6 && !history.CanRedo,
                "Every functional mutation must create one command.");
            for (int index = 0; index < 6; index++)
                Assert(history.TryUndo(fixture.Graph, false, out _),
                    "Every command in the chain must be undoable.");
            arrival = fixture.Graph.Sources.First(item => item.ElementKey == "arrival");
            boundary = fixture.Graph.Sources.First(item => item.ElementKey == "boundary");
            Assert(arrival.IsActive && boundary.EntryConnectorIndex <
                boundary.ExitConnectorIndex,
                "Undo must restore source activation and direction.");
            Assert(!valve.IsClosed,
                "Undo must restore the valve state.");
            Assert(fixture.Graph.DirectionConstraints.Count == 0 &&
                fixture.Graph.Sources.All(item => item.Name != "manual source"),
                "Undo must remove added constraints and sources.");

            for (int index = 0; index < 6; index++)
                Assert(history.TryRedo(fixture.Graph, false, out _),
                    "Every undone command must be redoable.");
            Assert(fixture.Graph.Sources.Any(item => item.Name == "manual source") &&
                fixture.Graph.DirectionConstraints.Count == 2,
                "Redo must restore the complete final scenario.");
        }

        private static void HistoryMakesResetOneUndoableAction()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("valve", 2, valve: true);
            fixture.CloseValve("valve");
            fixture.Graph.FindValve("valve")!.WasManuallyOverridden = true;
            fixture.Graph.Sources.Add(new GameMepSourceData
            {
                ElementKey = "valve",
                Name = "temporary",
                IsActive = true,
                IsUserCreated = true,
                WasManuallyOverridden = true
            });
            fixture.SetDirectionConstraint("valve", 0, 1);
            var history = new GameMepScenarioHistory();

            Assert(history.Execute(fixture.Graph, "reset", () =>
            {
                fixture.Graph.FindValve("valve")!.IsClosed = false;
                foreach (GameMepSourceData source in fixture.Graph.Sources
                    .Where(item => item.IsUserCreated).ToList())
                    fixture.Graph.Sources.Remove(source);
                fixture.Graph.DirectionConstraints.Clear();
            }), "Reset must be captured as one command.");
            Assert(history.UndoCount == 1 &&
                !fixture.Graph.FindValve("valve")!.IsClosed,
                "Reset must result in exactly one undo entry.");
            Assert(history.TryUndo(fixture.Graph, false, out _) &&
                fixture.Graph.FindValve("valve")!.IsClosed &&
                fixture.Graph.Sources.Any(item => item.Name == "temporary") &&
                fixture.Graph.DirectionConstraints.Count == 1,
                "Undoing reset must restore the entire previous scenario.");
        }

        private static void HistoryRestoresRemovedSource()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("pipe", 2);
            var source = new GameMepSourceData
            {
                ElementKey = "pipe",
                Name = "removable",
                IsActive = true,
                IsUserCreated = true,
                WasManuallyOverridden = true
            };
            fixture.Graph.Sources.Add(source);
            var history = new GameMepScenarioHistory();

            Assert(history.Execute(fixture.Graph, "remove source",
                () => fixture.Graph.Sources.Remove(source)) &&
                fixture.Graph.Sources.Count == 0,
                "Removing an individual source must be recorded.");
            Assert(history.TryUndo(fixture.Graph, false, out _) &&
                fixture.Graph.Sources.Single().Name == "removable",
                "Undo must restore an individually removed source.");
            Assert(history.TryRedo(fixture.Graph, false, out _) &&
                fixture.Graph.Sources.Count == 0,
                "Redo must remove that source again.");
        }

        private static void HistoryClearsRedoAfterNewActionAndKeepsOneHundred()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            GameMepSourceData source = fixture.Graph.Sources.Single();
            var history = new GameMepScenarioHistory();
            for (int index = 0; index < 105; index++)
            {
                Assert(history.Execute(fixture.Graph, "toggle " + index,
                    () => source.IsActive = !source.IsActive),
                    "Each distinct toggle must be recorded.");
            }
            Assert(history.UndoCount == 100,
                "The undo stack must be bounded to one hundred commands.");
            Assert(history.TryUndo(fixture.Graph, false, out _) && history.CanRedo,
                "Undo must populate the redo stack.");
            Assert(history.Execute(fixture.Graph, "new branch",
                () =>
                {
                    GameMepSourceData current = fixture.Graph.Sources.Single();
                    current.IsActive = !current.IsActive;
                }) && !history.CanRedo,
                "A new action after undo must clear redo history.");
        }

        private static void HistoryRefusesOperationsWhileCalculationRuns()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            GameMepSourceData source = fixture.Graph.Sources.Single();
            var history = new GameMepScenarioHistory();
            Assert(!history.Execute(fixture.Graph, "busy",
                    () => source.IsActive = false,
                    calculationInProgress: true) && source.IsActive,
                "A mutation must not run during calculation.");
            history.Execute(fixture.Graph, "normal", () => source.IsActive = false);
            Assert(!history.TryUndo(fixture.Graph, true, out _) && !source.IsActive,
                "Undo must be refused cleanly during calculation.");
            Assert(history.TryUndo(fixture.Graph, false, out _) &&
                fixture.Graph.Sources.Single().IsActive,
                "Undo must work as soon as calculation is no longer running.");
        }

        private static void HistoryFinalStateIsWhatPersistenceCaptures()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("valve", 2, valve: true);
            fixture.Graph.FindElement("valve")!.PersistentId = "valve-persistent";
            GameMepValveData valve = fixture.Graph.FindValve("valve")!;
            valve.InitiallyEnabledAsValve = true;
            var history = new GameMepScenarioHistory();
            history.Execute(fixture.Graph, "close", () =>
            {
                valve.IsClosed = true;
                valve.WasManuallyOverridden = true;
            });
            Assert(GameMepScenarioStore.Capture(fixture.Graph).Valves.Count == 1,
                "The modified valve must be present before undo.");
            history.TryUndo(fixture.Graph, false, out _);

            GameMepScenarioSnapshot snapshot =
                GameMepScenarioStore.Capture(fixture.Graph);
            Assert(snapshot.Valves.Count == 0,
                "Persistence after undo must capture only the restored final state.");
        }

        private static void PartialValveResetPreservesSourcesAndDirections()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("valve", 2, valve: true);
            fixture.AddElement("pipe", 2);
            GameMepValveData valve = fixture.Graph.FindValve("valve")!;
            valve.InitiallyEnabledAsValve = true;
            valve.IsClosed = true;
            valve.WasManuallyOverridden = true;
            fixture.SetDirectedSource("pipe", 0, 1);
            fixture.Graph.Sources.Single().IsUserCreated = true;
            fixture.Graph.Sources.Single().WasManuallyOverridden = true;
            fixture.SetDirectionConstraint("pipe", 1, 0);
            var history = new GameMepScenarioHistory();

            Assert(history.Execute(fixture.Graph, "reset valves", () =>
                GameMepScenarioReset.ResetValvesToInitial(
                    fixture.Graph.Valves)),
                "A valve-only reset must be recorded.");
            Assert(!valve.IsClosed && valve.IsEnabledAsValve,
                "Valve-only reset must restore valve state.");
            Assert(fixture.Graph.Sources.Count == 1 &&
                fixture.Graph.DirectionConstraints.Count == 1,
                "Valve-only reset must preserve sources and direction corrections.");
            Assert(history.TryUndo(fixture.Graph, false, out _) && valve.IsClosed,
                "Valve-only reset must be undoable.");
        }

        private static void PartialSourceResetPreservesValves()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("valve", 2, valve: true);
            fixture.AddElement("pipe", 2);
            GameMepValveData valve = fixture.Graph.FindValve("valve")!;
            valve.IsClosed = true;
            valve.WasManuallyOverridden = true;
            fixture.SetDirectedSource("pipe", 0, 1);
            fixture.Graph.Sources.Single().IsUserCreated = true;
            fixture.Graph.Sources.Single().WasManuallyOverridden = true;
            fixture.SetDirectionConstraint("pipe", 1, 0);
            var history = new GameMepScenarioHistory();

            Assert(history.Execute(fixture.Graph, "reset sources", () =>
                GameMepScenarioReset.ResetSourcesAndDirections(
                    fixture.Graph, element => true)),
                "A source-and-direction reset must be recorded.");
            Assert(fixture.Graph.Sources.Count == 0 &&
                fixture.Graph.DirectionConstraints.Count == 0,
                "Source reset must remove manual sources and corrections.");
            Assert(valve.IsClosed,
                "Source reset must preserve valve states.");
            Assert(history.TryUndo(fixture.Graph, false, out _) &&
                fixture.Graph.Sources.Count == 1 &&
                fixture.Graph.DirectionConstraints.Count == 1 && valve.IsClosed,
                "Source reset must be fully undoable without touching valves.");
        }

        private static void SystemResetOnlyTouchesSelectedSystem()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.Graph.Systems.Add(new GameMepSystemData
            {
                Key = "system-b",
                Name = "System B"
            });
            fixture.AddElement("valve-a", 2, valve: true);
            fixture.AddElement("valve-b", 2, valve: true);
            fixture.AddElement("pipe-a", 2);
            fixture.AddElement("pipe-b", 2);
            foreach (string key in new[] { "valve-b", "pipe-b" })
            {
                GameMepElementData element = fixture.Graph.FindElement(key)!;
                element.SystemKey = "system-b";
                element.SystemName = "System B";
                foreach (int connector in element.ConnectorIndices)
                    fixture.Graph.Connectors[connector].SystemKey = "system-b";
            }
            GameMepValveData valveA = fixture.Graph.FindValve("valve-a")!;
            GameMepValveData valveB = fixture.Graph.FindValve("valve-b")!;
            valveA.InitiallyEnabledAsValve = true;
            valveB.InitiallyEnabledAsValve = true;
            valveA.IsClosed = true;
            valveB.IsClosed = true;
            fixture.SetDirectedSource("pipe-a", 0, 1);
            fixture.SetDirectedSource("pipe-b", 0, 1);
            foreach (GameMepSourceData source in fixture.Graph.Sources)
            {
                source.IsUserCreated = true;
                source.WasManuallyOverridden = true;
            }
            fixture.Graph.Sources.First(item => item.ElementKey == "pipe-b")
                .SystemKey = "system-b";
            fixture.SetDirectionConstraint("pipe-a", 1, 0);
            fixture.SetDirectionConstraint("pipe-b", 1, 0);
            var history = new GameMepScenarioHistory();

            Assert(history.Execute(fixture.Graph, "reset system a", () =>
            {
                Func<GameMepElementData, bool> inSystemA = element =>
                    GameMepScenarioReset.ElementBelongsToSystem(
                        fixture.Graph, element, "test-system");
                GameMepScenarioReset.ResetValvesToInitial(
                    fixture.Graph.Valves.Where(item =>
                    {
                        GameMepElementData? element =
                            fixture.Graph.FindElement(item.ElementKey);
                        return element != null && inSystemA(element);
                    }));
                GameMepScenarioReset.ResetSourcesAndDirections(
                    fixture.Graph, inSystemA);
            }), "A selected-system reset must be recorded.");
            Assert(!valveA.IsClosed && valveB.IsClosed,
                "Only valves in the selected system may be reset.");
            Assert(fixture.Graph.Sources.All(item => item.ElementKey != "pipe-a") &&
                fixture.Graph.Sources.Any(item => item.ElementKey == "pipe-b"),
                "Only sources in the selected system may be removed.");
            Assert(fixture.Graph.DirectionConstraints.All(item =>
                    item.ElementKey != "pipe-a") &&
                fixture.Graph.DirectionConstraints.Any(item =>
                    item.ElementKey == "pipe-b"),
                "Only direction corrections in the selected system may be removed.");
            Assert(history.TryUndo(fixture.Graph, false, out _) &&
                valveA.IsClosed && valveB.IsClosed &&
                fixture.Graph.Sources.Count == 2 &&
                fixture.Graph.DirectionConstraints.Count == 2,
                "A selected-system reset must restore everything on undo.");
        }

        private static void NetworkTraceFollowsSourceAndDownstream()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("pipe", 2);
            fixture.AddElement("terminal", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("source", 0, "pipe", 0);
            fixture.Connect("pipe", 1, "terminal", 0);
            fixture.Connect("terminal", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);
            fixture.Calculate();

            GameMepNetworkTraceResult downstream = GameMepNetworkTracer.Build(
                fixture.Graph, "source", GameMepTraceMode.Downstream);
            GameMepNetworkTraceResult upstream = GameMepNetworkTracer.Build(
                fixture.Graph, "terminal", GameMepTraceMode.Upstream);

            Assert(downstream.ElementKeys.Contains("source") &&
                downstream.ElementKeys.Contains("pipe") &&
                downstream.ElementKeys.Contains("terminal"),
                "Downstream trace must follow the resolved circulation.");
            Assert(upstream.ElementKeys.Contains("source") &&
                upstream.ElementKeys.Contains("pipe") &&
                upstream.ElementKeys.Contains("terminal"),
                "Upstream trace must reuse the primary-source provenance.");
        }

        private static void NetworkTraceExposesAndSelectsTeeBranches()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("trunk", 2);
            fixture.AddElement("tee", 3);
            fixture.AddElement("branch-a", 2);
            fixture.AddElement("branch-b", 2);
            fixture.AddElement("return-a", 1);
            fixture.AddElement("return-b", 1);
            fixture.Connect("source", 0, "trunk", 0);
            fixture.Connect("trunk", 1, "tee", 0);
            fixture.Connect("tee", 1, "branch-a", 0);
            fixture.Connect("tee", 2, "branch-b", 0);
            fixture.Connect("branch-a", 1, "return-a", 0);
            fixture.Connect("branch-b", 1, "return-b", 0);
            fixture.AddBoundary("return-a", GameMepBoundaryKind.Outlet);
            fixture.AddBoundary("return-b", GameMepBoundaryKind.Outlet);
            fixture.Calculate();

            GameMepNetworkTraceResult all = GameMepNetworkTracer.Build(
                fixture.Graph, "trunk", GameMepTraceMode.Downstream);
            Assert(all.Branches.Count == 2 &&
                all.ElementKeys.Contains("branch-a") &&
                all.ElementKeys.Contains("branch-b"),
                "A tee must expose every accessible downstream branch.");

            GameMepTraceBranchData branchA = all.Branches.First(item =>
                item.ElementKeys.Contains("branch-a"));
            GameMepNetworkTraceResult selected = GameMepNetworkTracer.Build(
                fixture.Graph,
                "trunk",
                GameMepTraceMode.Downstream,
                branchA.ElementKey);
            Assert(selected.ElementKeys.Contains("trunk") &&
                selected.ElementKeys.Contains("tee") &&
                selected.ElementKeys.Contains("branch-a") &&
                !selected.ElementKeys.Contains("branch-b"),
                "Selecting one tee branch must dim the sibling branch.");
        }

        private static void NetworkTraceKeepsBypassAroundClosedValve()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("valve", 2, valve: true);
            fixture.AddElement("bypass", 2);
            fixture.AddElement("target", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("source", 0, "valve", 0);
            fixture.Connect("source", 0, "bypass", 0);
            fixture.Connect("valve", 1, "target", 0);
            fixture.Connect("bypass", 1, "target", 0);
            fixture.Connect("target", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);
            fixture.CloseValve("valve");
            fixture.Calculate();

            GameMepNetworkTraceResult trace = GameMepNetworkTracer.Build(
                fixture.Graph, "source", GameMepTraceMode.Downstream);
            Assert(trace.ElementKeys.Contains("bypass") &&
                trace.ElementKeys.Contains("target"),
                "A closed valve must not hide a supplied alternative bypass.");
        }

        private static void NetworkTraceBranchSelectionSurvivesRejoinedLoop()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("trunk", 2);
            fixture.AddElement("tee", 3);
            fixture.AddElement("branch-a", 2);
            fixture.AddElement("branch-b", 2);
            fixture.AddElement("target", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("source", 0, "trunk", 0);
            fixture.Connect("trunk", 1, "tee", 0);
            fixture.Connect("tee", 1, "branch-a", 0);
            fixture.Connect("tee", 2, "branch-b", 0);
            fixture.Connect("branch-a", 1, "target", 0);
            fixture.Connect("branch-b", 1, "target", 0);
            fixture.Connect("target", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);
            fixture.Calculate();

            GameMepNetworkTraceResult all = GameMepNetworkTracer.Build(
                fixture.Graph, "trunk", GameMepTraceMode.Downstream);
            GameMepTraceBranchData branchA = all.Branches.First(branch =>
                branch.ElementKeys.Contains("branch-a") &&
                !string.Equals(branch.ElementKey, "branch-b", StringComparison.Ordinal));
            GameMepNetworkTraceResult selected = GameMepNetworkTracer.Build(
                fixture.Graph,
                "trunk",
                GameMepTraceMode.Downstream,
                branchA.ElementKey);
            Assert(selected.ElementKeys.Contains("target"),
                "A selected branch must retain the common network after a loop rejoins.");
            Assert(!selected.ElementKeys.Contains("branch-b"),
                "A selected loop branch must keep its parallel sibling attenuated.");
        }

        private static void NetworkTraceHonorsHiddenSystems()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("visible", 2);
            fixture.AddElement("hidden", 2);
            fixture.Connect("source", 0, "visible", 0);
            fixture.Connect("visible", 1, "hidden", 0);
            fixture.Graph.Systems.Add(new GameMepSystemData
            {
                Key = "hidden-system",
                Name = "Hidden system",
                IsVisible = false
            });
            fixture.Graph.FindElement("hidden")!.SystemKey = "hidden-system";
            fixture.SetConnectorSystem("hidden", 0, "hidden-system");
            fixture.SetConnectorSystem("hidden", 1, "hidden-system");
            fixture.Calculate();

            GameMepNetworkTraceResult trace = GameMepNetworkTracer.Build(
                fixture.Graph, "source", GameMepTraceMode.Downstream);
            Assert(trace.ElementKeys.Contains("visible") &&
                !trace.ElementKeys.Contains("hidden"),
                "Tracing must respect the current system visibility filters.");
        }

        private static void NetworkTraceStaysFastOnLargeGraph()
        {
            const int pipeCount = 1200;
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            string previous = "source";
            int previousPort = 0;
            for (int index = 0; index < pipeCount; index++)
            {
                string key = "pipe-" + index.ToString("D4");
                fixture.AddElement(key, 2);
                fixture.Connect(previous, previousPort, key, 0);
                previous = key;
                previousPort = 1;
            }
            fixture.AddElement("return", 1);
            fixture.Connect(previous, previousPort, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);
            fixture.Calculate();
            Assert(fixture.Graph.LastCalculationMilliseconds < 1000.0,
                "Potential calculation must stay outside the render-loop budget on a large graph.");

            var stopwatch = Stopwatch.StartNew();
            GameMepNetworkTraceResult trace = GameMepNetworkTracer.Build(
                fixture.Graph, "source", GameMepTraceMode.Downstream);
            stopwatch.Stop();
            Assert(trace.ElementKeys.Count == pipeCount + 2,
                "Large-network tracing must retain every reachable element.");
            Assert(stopwatch.ElapsedMilliseconds < 1000,
                "Large-network tracing must remain outside the render-loop budget.");
        }

        private static void DiagnosticsClassifyCriticalDirectionConflict()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("arrival-a", 1, source: true);
            fixture.AddElement("pipe", 2);
            fixture.AddElement("arrival-b", 1, source: true);
            fixture.Connect("arrival-a", 0, "pipe", 0);
            fixture.Connect("arrival-b", 0, "pipe", 1);

            fixture.Calculate();

            Assert(fixture.Graph.Diagnostics.Any(item =>
                    item.Kind == GameMepDiagnosticKind.DirectionConflict &&
                    item.Severity == GameMepDiagnosticSeverity.Critical &&
                    item.ElementKey == "pipe"),
                "A contradictory direction must create a localized critical diagnostic.");
        }

        private static void DiagnosticsDetectAmbiguousFlowControl()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("maybe-valve", 2);
            fixture.AddFlowControlCandidate(
                "maybe-valve",
                GameMepConfidence.Medium,
                enabled: true);
            fixture.Connect("source", 0, "maybe-valve", 0);

            fixture.Calculate();

            Assert(fixture.Graph.Diagnostics.Any(item =>
                    item.Kind == GameMepDiagnosticKind.AmbiguousFlowControl &&
                    item.Severity == GameMepDiagnosticSeverity.Warning &&
                    item.ShowInSmartMode),
                "A medium-confidence flow control must be visible in smart diagnostics.");
        }

        private static void DiagnosticsDetectUnknownPassThroughComponent()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("mystery-box", 2);
            fixture.AddElement("terminal", 1);
            fixture.Connect("source", 0, "mystery-box", 0);
            fixture.Connect("mystery-box", 1, "terminal", 0);

            fixture.Calculate();

            Assert(fixture.Graph.Diagnostics.Any(item =>
                    item.Kind == GameMepDiagnosticKind.UnknownPassThroughComponent &&
                    item.IsAggregate && item.ShowInSmartMode &&
                    item.ElementKey == "mystery-box"),
                "An unknown two-port component must be grouped and localized.");
        }

        private static void DiagnosticsDetectDisconnectedElement()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("detached", 2);

            fixture.Calculate();

            Assert(fixture.Graph.Diagnostics.Any(item =>
                    item.Kind == GameMepDiagnosticKind.DisconnectedElement &&
                    item.Severity == GameMepDiagnosticSeverity.Warning &&
                    item.ElementKey == "detached"),
                "A multi-port element with no physical connection must be reported.");
        }

        private static void DiagnosticsGroupOpenConnectorsInSmartMode()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("pipe-a", 2);
            fixture.AddElement("pipe-b", 2);
            fixture.Connect("source", 0, "pipe-a", 0);
            fixture.Connect("pipe-a", 1, "pipe-b", 0);

            fixture.Calculate();

            IList<GameMepDiagnosticData> detailed = fixture.Graph.Diagnostics
                .Where(item => item.Kind == GameMepDiagnosticKind.OpenConnector &&
                    !item.IsAggregate)
                .ToList();
            IList<GameMepDiagnosticData> smart = fixture.Graph.Diagnostics
                .Where(item => item.Kind == GameMepDiagnosticKind.OpenConnector &&
                    item.IsAggregate && item.ShowInSmartMode)
                .ToList();
            Assert(detailed.Count == 1,
                "The exhaustive diagnostic must retain the individual open connector.");
            Assert(smart.Count == 1 && smart[0].OccurrenceCount == 1,
                "Smart mode must expose one grouped open-connector diagnostic per branch.");
        }

        private static void DiagnosticsDetectBranchWithoutSource()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("orphan-a", 2);
            fixture.AddElement("orphan-b", 2);
            fixture.Connect("orphan-a", 1, "orphan-b", 0);

            fixture.Calculate();

            GameMepDiagnosticData diagnostic = fixture.Graph.Diagnostics.Single(item =>
                item.Kind == GameMepDiagnosticKind.BranchWithoutSource);
            Assert(diagnostic.OccurrenceCount == 2 && diagnostic.ShowInSmartMode,
                "A source-less connected component must be grouped as one branch diagnostic.");
        }

        private static void DiagnosticsDetectIncompatibleSystems()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("first", 1, source: true);
            fixture.AddElement("second", 1);
            fixture.SetConnectorSystem("first", 0, "system-hot");
            fixture.SetConnectorSystem("second", 0, "system-cold");
            fixture.Connect("first", 0, "second", 0);

            fixture.Calculate();

            Assert(fixture.Graph.Diagnostics.Any(item =>
                    item.Kind == GameMepDiagnosticKind.IncompatibleSystems &&
                    item.IsAggregate && item.OccurrenceCount == 1),
                "A physical connection between different Revit systems must be reported.");
        }

        private static void DiagnosticsReportInvalidSavedSettings()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("pipe", 2);
            fixture.Graph.SkippedScenarioEntryCount = 3;

            fixture.Calculate();

            GameMepDiagnosticData diagnostic = fixture.Graph.Diagnostics.Single(item =>
                item.Kind == GameMepDiagnosticKind.InvalidSavedSetting);
            Assert(diagnostic.OccurrenceCount == 3 &&
                diagnostic.Severity == GameMepDiagnosticSeverity.Warning,
                "Invalid persisted entries must produce one non-blocking grouped warning.");
        }

        private static void EmptyGraphDoesNotFail()
        {
            var graph = new GameMepGraphData();
            new GameMepSimulationEngine(graph).Recalculate();
            Assert(graph.LastCalculationMilliseconds >= 0.0,
                "An empty Revit model must produce an empty, valid graph.");
        }

        private static void ReplaySnapshotRoundTripPreservesGraphAndCalculation()
        {
            string directory = CreateTestDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                string filePath = Path.Combine(directory, "tee.bimaestro-mep.json");
                var fixture = new GraphFixture();
                fixture.Graph.DocumentTitle = "Maquette hydraulique";
                fixture.Graph.ScenarioModelKey = "FILE|C:/secret/project.rvt";
                fixture.Graph.ScenarioCanPersist = true;
                fixture.AddElement("source", 2, source: true);
                fixture.AddElement("pipe-in", 2);
                fixture.AddElement("tee", 3);
                fixture.MarkPipeFitting("tee");
                fixture.AddJunctionPaths("tee");
                fixture.AddElement("pipe-main", 2);
                fixture.AddElement("pipe-branch", 2);
                fixture.Connect("source", 1, "pipe-in", 0);
                fixture.Connect("pipe-in", 1, "tee", 0);
                fixture.Connect("tee", 1, "pipe-main", 0);
                fixture.Connect("tee", 2, "pipe-branch", 0);
                fixture.SetPathLength("source", 1.0);
                fixture.SetPathLength("pipe-in", 4.0);
                fixture.SetPathLength("pipe-main", 6.0);
                fixture.SetPathLength("pipe-branch", 2.0);
                fixture.SetPipeDiameter("pipe-in", 0.6);
                fixture.SetPipeDiameter("pipe-main", 0.6);
                fixture.SetPipeDiameter("pipe-branch", 0.2);
                fixture.Graph.Elements[0].PersistentId = "revit-unique-id";
                fixture.Graph.Connectors[0].PersistentKey = "revit-connector-id";
                new GameMepSimulationEngine(fixture.Graph).Recalculate();

                GameMepReplayStore.Save(fixture.Graph, filePath);
                GameMepReplaySnapshot loaded = GameMepReplayStore.Load(filePath);
                GameMepReplayResult replayed = GameMepReplayStore.Replay(loaded);

                Assert(loaded.Graph.Elements.Count == fixture.Graph.Elements.Count,
                    "Replay export must preserve every MEP element.");
                Assert(loaded.Graph.Connectors.Count == fixture.Graph.Connectors.Count,
                    "Replay export must preserve every connector.");
                Assert(loaded.Graph.Connections.Count == fixture.Graph.Connections.Count,
                    "Replay export must preserve every graph connection.");
                Assert(loaded.CapturedPathStates.Count ==
                        fixture.Graph.Elements.Sum(element => element.Paths.Count),
                    "Replay export must preserve the captured direction of every path.");
                Assert(loaded.Graph.ScenarioModelKey.Length == 0 &&
                    !loaded.Graph.ScenarioCanPersist,
                    "Replay export must not expose the local Revit model path.");
                Assert(loaded.Graph.Elements.All(element =>
                        string.IsNullOrEmpty(element.PersistentId)) &&
                    loaded.Graph.Connectors.All(connector =>
                        string.IsNullOrEmpty(connector.PersistentKey)),
                    "Replay export must remove persistent Revit identifiers.");
                Assert(replayed.PathCount == fixture.Graph.Elements.Sum(element =>
                        element.Paths.Count),
                    "Replay must execute every exported path.");
                Assert(replayed.StateChangeCount == 0,
                    "An unchanged engine must reproduce the captured graph exactly.");
                Assert(replayed.CapturedVisibleDiscontinuityCount == 0 &&
                    replayed.ReplayedVisibleDiscontinuityCount == 0,
                    "An unchanged tee graph must keep visible paths continuous.");
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        private static void ScenarioRoundTripRestoresSourcesAndValves()
        {
            string directory = CreateTestDirectory();
            try
            {
                var saved = new PersistenceFixture("FILE|C:/PROJECT-A.RVT");
                saved.AddElement("source-a-old", "uid-source-a", "a-in", "a-out");
                saved.AddElement("source-b-old", "uid-source-b", "b-in", "b-out");
                saved.AddElement("automatic-old", "uid-auto", "auto-port");
                saved.AddElement("valve-a-old", "uid-valve-a", "va-0", "va-1");
                saved.AddElement("valve-b-old", "uid-valve-b", "vb-0", "vb-1");
                saved.AddElement("pump-old", "uid-pump", "pump-in", "pump-out");
                saved.AddSource("source-a-old", true, true, 0, 1);
                saved.AddSource("source-b-old", true, true, 1, 0,
                    boundaryKind: GameMepBoundaryKind.Outlet);
                saved.AddSource("automatic-old", false, false, -1, -1, initiallyActive: true);
                saved.AddValve("valve-a-old", true, true, initiallyEnabled: true);
                saved.AddValve("valve-b-old", false, false, initiallyEnabled: true);
                saved.AddDirectionConstraint("pump-old", 0, 1);

                Assert(GameMepScenarioStore.SaveNow(saved.Graph, directory),
                    "A valid scenario must be written.");
                Assert(File.Exists(GameMepScenarioStore.GetScenarioFilePath(
                    saved.Graph,
                    directory)), "The model scenario file must exist.");

                // Les clés d'exécution et les indices changent volontairement :
                // seuls les identifiants Revit persistants doivent être utilisés.
                var restored = new PersistenceFixture("FILE|C:/PROJECT-A.RVT");
                restored.AddElement("dummy", "uid-dummy", "dummy-port");
                restored.AddElement("source-a-new", "uid-source-a", "a-in", "a-out");
                restored.AddElement("source-b-new", "uid-source-b", "b-in", "b-out");
                restored.AddElement("automatic-new", "uid-auto", "auto-port");
                restored.AddElement("valve-a-new", "uid-valve-a", "va-0", "va-1");
                restored.AddElement("valve-b-new", "uid-valve-b", "vb-0", "vb-1");
                restored.AddElement("pump-new", "uid-pump", "pump-in", "pump-out");
                restored.AddSource("automatic-new", true, false, -1, -1, initiallyActive: true);
                restored.AddValve("valve-a-new", true, false, initiallyEnabled: true);
                restored.AddValve("valve-b-new", true, false, initiallyEnabled: true);

                GameMepScenarioRestoreResult result =
                    GameMepScenarioStore.Restore(restored.Graph, directory);
                Assert(result.Error.Length == 0, "A valid scenario must restore without error.");
                Assert(result.RestoredSources == 3, "Three source states must be restored.");
                Assert(result.RestoredValves == 2,
                    "Two valve states must be restored.");
                Assert(result.RestoredDirectionConstraints == 1,
                    "The pump direction constraint must be restored.");

                GameMepSourceData sourceA = restored.Graph.Sources.First(source =>
                    source.ElementKey == "source-a-new");
                GameMepSourceData sourceB = restored.Graph.Sources.First(source =>
                    source.ElementKey == "source-b-new");
                GameMepSourceData automatic = restored.Graph.Sources.First(source =>
                    source.ElementKey == "automatic-new");
                Assert(sourceA.IsActive && sourceA.IsUserCreated,
                    "The first user source must be recreated and active.");
                Assert(restored.Graph.Connectors[sourceA.EntryConnectorIndex].PersistentKey == "a-in" &&
                    restored.Graph.Connectors[sourceA.ExitConnectorIndex].PersistentKey == "a-out",
                    "The first source direction must survive connector reindexing.");
                Assert(restored.Graph.Connectors[sourceB.EntryConnectorIndex].PersistentKey == "b-out" &&
                    restored.Graph.Connectors[sourceB.ExitConnectorIndex].PersistentKey == "b-in",
                    "The return source direction must be restored independently.");
                Assert(sourceB.BoundaryKind == GameMepBoundaryKind.Outlet,
                    "The persisted return must not be restored as a supplying arrival.");
                GameMepDirectionConstraintData pumpConstraint =
                    restored.Graph.DirectionConstraints.Single();
                Assert(pumpConstraint.ElementKey == "pump-new" &&
                    pumpConstraint.Scope ==
                        GameMepDirectionConstraintScope.EquipmentPressureRise &&
                    restored.Graph.Connectors[pumpConstraint.EntryConnectorIndex].PersistentKey == "pump-in" &&
                    restored.Graph.Connectors[pumpConstraint.ExitConnectorIndex].PersistentKey == "pump-out",
                    "The pump direction must survive connector reindexing.");
                Assert(!automatic.IsActive,
                    "A manually disabled automatic source must remain disabled.");
                Assert(restored.Graph.FindValve("valve-a-new")!.IsClosed,
                    "A closed valve must remain closed.");
                Assert(!restored.Graph.FindValve("valve-b-new")!.IsEnabledAsValve,
                    "A manually rejected valve must stay rejected.");
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        private static void NamedScenariosCanBeSavedLoadedAndDeleted()
        {
            string directory = CreateTestDirectory();
            try
            {
                var fixture = new PersistenceFixture("FILE|C:/NAMED-SCENARIOS.RVT");
                fixture.AddElement("valve", "uid-named-valve", "n0", "n1");
                fixture.AddValve("valve", true, false, initiallyEnabled: true);

                GameMepScenarioStore.SaveNamed(
                    fixture.Graph, "Fonctionnement normal", directory);
                fixture.Graph.FindValve("valve")!.IsClosed = true;
                fixture.Graph.FindValve("valve")!.WasManuallyOverridden = true;
                GameMepScenarioStore.SaveNamed(
                    fixture.Graph, "Maintenance", directory);

                IList<GameMepNamedScenarioInfo> scenarios =
                    GameMepScenarioStore.ListNamed(fixture.Graph, directory);
                Assert(scenarios.Count == 2 &&
                    scenarios.Any(item => item.Name == "Fonctionnement normal") &&
                    scenarios.Any(item => item.Name == "Maintenance"),
                    "Named scenarios must be listed independently for the model.");

                GameMepScenarioStore.RestoreNamed(
                    fixture.Graph, "Fonctionnement normal", directory);
                Assert(!fixture.Graph.FindValve("valve")!.IsClosed,
                    "Loading the normal scenario must reopen the valve.");
                GameMepScenarioStore.RestoreNamed(
                    fixture.Graph, "Maintenance", directory);
                Assert(fixture.Graph.FindValve("valve")!.IsClosed,
                    "Loading the maintenance scenario must restore the closed valve.");

                Assert(GameMepScenarioStore.DeleteNamed(
                        fixture.Graph, "Maintenance", directory) &&
                    GameMepScenarioStore.ListNamed(fixture.Graph, directory).Count == 1,
                    "Deleting one named scenario must preserve the other one.");
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        private static void ResetRemovesPersistedScenario()
        {
            string directory = CreateTestDirectory();
            try
            {
                var fixture = new PersistenceFixture("FILE|C:/RESET.RVT");
                fixture.AddElement("source", "uid-reset-source", "r0", "r1");
                fixture.AddSource("source", true, true, 0, 1);
                GameMepScenarioStore.SaveNow(fixture.Graph, directory);
                string path = GameMepScenarioStore.GetScenarioFilePath(
                    fixture.Graph,
                    directory);
                Assert(File.Exists(path), "A modified scenario must exist before reset.");

                fixture.Graph.Sources.Clear();
                GameMepScenarioStore.SaveNow(fixture.Graph, directory);
                Assert(!File.Exists(path) && !File.Exists(path + ".bak"),
                    "Reset must remove both the active scenario and its backup.");
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        private static void ChangedNetworkSkipsInvalidDirection()
        {
            string directory = CreateTestDirectory();
            try
            {
                var saved = new PersistenceFixture("CENTRAL|SERVER/CHANGED.RVT");
                saved.AddElement("source-old", "uid-changed", "old-in", "old-out");
                saved.AddSource("source-old", true, true, 0, 1);
                GameMepScenarioStore.SaveNow(saved.Graph, directory);

                var changed = new PersistenceFixture("CENTRAL|SERVER/CHANGED.RVT");
                changed.AddElement("source-new", "uid-changed", "old-in", "new-out");
                GameMepScenarioRestoreResult result =
                    GameMepScenarioStore.Restore(changed.Graph, directory);
                Assert(result.SkippedEntries == 1,
                    "A source whose connector disappeared must be reported as skipped.");
                Assert(changed.Graph.Sources.Count == 0,
                    "An invalid directed source must not become bidirectional.");
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        private static void ChangedNetworkSkipsLegacyPipeFittingSource()
        {
            string directory = CreateTestDirectory();
            try
            {
                var saved = new PersistenceFixture("CENTRAL|SERVER/LEGACY-FITTING.RVT");
                saved.AddElement("old-boundary", "uid-legacy-fitting", "p0", "p1", "p2");
                saved.AddSource("old-boundary", true, true, -1, -1);
                GameMepScenarioStore.SaveNow(saved.Graph, directory);

                var restored = new PersistenceFixture("CENTRAL|SERVER/LEGACY-FITTING.RVT");
                restored.AddElement("CML_Acier Té", "uid-legacy-fitting", "p0", "p1", "p2");
                restored.Graph.FindElement("CML_Acier Té")!.IsPipeFitting = true;
                restored.Graph.FindElement("CML_Acier Té")!.IsPipeJunction = true;

                GameMepScenarioRestoreResult result =
                    GameMepScenarioStore.Restore(restored.Graph, directory);

                Assert(result.SkippedEntries == 1 &&
                    result.RestoredSources == 0 &&
                    restored.Graph.Sources.Count == 0,
                    "A legacy scenario must never restore a tee as a hydraulic source.");
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        private static void ScenarioFilesAreIsolatedByModel()
        {
            string directory = CreateTestDirectory();
            try
            {
                var first = new PersistenceFixture("FILE|C:/A/SAME-NAME.RVT", "Same name");
                first.AddElement("source", "uid-isolated", "i0", "i1");
                first.AddSource("source", true, true, 0, 1);
                GameMepScenarioStore.SaveNow(first.Graph, directory);

                var second = new PersistenceFixture("FILE|D:/B/SAME-NAME.RVT", "Same name");
                second.AddElement("source", "uid-isolated", "i0", "i1");
                GameMepScenarioRestoreResult result =
                    GameMepScenarioStore.Restore(second.Graph, directory);
                Assert(result.RestoredSources == 0 && second.Graph.Sources.Count == 0,
                    "Two models with the same title must not share a scenario.");
                Assert(GameMepScenarioStore.GetScenarioFilePath(first.Graph, directory) !=
                    GameMepScenarioStore.GetScenarioFilePath(second.Graph, directory),
                    "Different model identities must produce different files.");
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        private static void UnsavedModelPersistsOnlyInCurrentSession()
        {
            string directory = CreateTestDirectory();
            try
            {
                var unsaved = new PersistenceFixture("session|42|Unsaved", canPersist: false);
                unsaved.AddElement("source-old", "uid-session", "s0", "s1");
                unsaved.AddSource("source-old", true, true, 0, 1);
                GameMepScenarioStore.SaveNow(unsaved.Graph, directory);

                var reopened = new PersistenceFixture("session|42|Unsaved", canPersist: false);
                reopened.AddElement("source-new", "uid-session", "s0", "s1");
                GameMepScenarioRestoreResult result =
                    GameMepScenarioStore.Restore(reopened.Graph, directory);
                Assert(result.RestoredSources == 1,
                    "An unsaved model must survive a window restart in the same Revit session.");
                Assert(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0,
                    "An unsaved model must never create a scenario file on disk.");
            }
            finally
            {
                DeleteTestDirectory(directory);
            }
        }

        private static string CreateTestDirectory()
        {
            return Path.Combine(
                Path.GetTempPath(),
                "BIMaestro-MepScenarioTests-" + Guid.NewGuid().ToString("N"));
        }

        private static void DeleteTestDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch
            {
            }
        }

        private static void SelectionIndexChoosesNearestPreciseTriangle()
        {
            GameElementData near = CreateSelectablePlane("near", 4.0, 0.0);
            GameElementData far = CreateSelectablePlane("far", 8.0, 0.0);
            var index = new GameSelectionIndex(new[] { far, near });
            GameSelectionHit? hit = index.FindNearest(
                new Point3D(0, 0, 0), new Vector3D(1, 0, 0));
            Assert(hit != null && hit.IsPrecise && hit.Element.Key == "near",
                "Selection must return the nearest exact triangle, independently of input order.");
        }

        private static void SelectionIndexRedirectsInsulationToPipe()
        {
            GameElementData pipe = CreateSelectablePlane("pipe", 6.0, 0.0);
            GameElementData insulation = CreateSelectablePlane("insulation", 4.0, 0.0);
            insulation.SelectionTargetKey = pipe.Key;
            var index = new GameSelectionIndex(new[] { insulation, pipe });
            GameSelectionHit? hit = index.FindNearest(
                new Point3D(0, 0, 0), new Vector3D(1, 0, 0));
            Assert(hit != null && hit.Element.Key == "pipe" && hit.Distance < 5.0,
                "Insulation geometry must redirect selection to its owning pipe.");
        }

        private static void SelectionIndexPrefersPreciseGeometryToBoundsFallback()
        {
            var fallback = new GameElementData { Key = "fallback", Name = "fallback" };
            fallback.Include(new[]
            {
                new Point3D(2, -2, -2),
                new Point3D(3, 2, 2)
            });
            GameElementData precise = CreateSelectablePlane("precise", 6.0, 0.0);
            var index = new GameSelectionIndex(new[] { fallback, precise });
            GameSelectionHit? hit = index.FindNearest(
                new Point3D(0, 0, 0), new Vector3D(1, 0, 0));
            Assert(hit != null && hit.IsPrecise && hit.Element.Key == "precise",
                "A bounds-only object must never mask a later exact surface.");
        }

        private static void SelectionIndexStaysWithinHoverBudget()
        {
            var elements = new List<GameElementData>();
            for (int index = 0; index < 10000; index++)
            {
                double x = 5.0 + index % 100;
                double y = (index / 100) * 3.0;
                elements.Add(CreateSelectablePlane("bulk-" + index, x, y));
            }
            var selection = new GameSelectionIndex(elements);
            selection.FindNearest(new Point3D(0, 0, 0), new Vector3D(1, 0, 0));
            var samples = new List<double>();
            for (int sample = 0; sample < 80; sample++)
            {
                var watch = Stopwatch.StartNew();
                selection.FindNearest(
                    new Point3D(0, sample % 20 * 3.0, 0),
                    new Vector3D(1, 0, 0));
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            double percentile95 = samples.OrderBy(value => value)
                .ElementAt((int)Math.Floor((samples.Count - 1) * 0.95));
            Assert(percentile95 < 12.0,
                "Selection BVH p95 must remain below 12 ms; measured " +
                percentile95.ToString("0.00") + " ms.");
        }

        private static GameElementData CreateSelectablePlane(
            string key,
            double x,
            double centerY)
        {
            var element = new GameElementData
            {
                Key = key,
                Name = key,
                Category = "Canalisations"
            };
            var triangle = new GameTriangle(
                new Point3D(x, centerY - 1.0, -1.0),
                new Point3D(x, centerY + 1.0, -1.0),
                new Point3D(x, centerY, 1.0),
                new Vector3D(-1, 0, 0),
                false);
            element.SelectionTriangles.Add(triangle);
            element.Include(new[] { triangle.A, triangle.B, triangle.C });
            return element;
        }

        private static void AssertState(
            GraphFixture fixture,
            string key,
            GameMepFlowState expected)
        {
            GameMepFlowState actual = fixture.Graph.FindElement(key)!.FlowState;
            Assert(actual == expected,
                key + ": expected " + expected + ", received " + actual + ".");
        }

        private static void AssertRenderableFlow(GraphFixture fixture, string key)
        {
            GameMepPathData path = fixture.Path(key);
            Assert(path.IsVisible &&
                path.Points.Count >= 2 &&
                path.FlowState == GameMepFlowState.Supplied &&
                path.HasCirculation &&
                path.DirectionState == GameMepDirectionState.Resolved &&
                path.Length > 0.02 &&
                !double.IsNaN(path.Length) &&
                !double.IsInfinity(path.Length) &&
                (fixture.Graph.FindSystem(path.SystemKey)?.IsVisible ?? true),
                key + ": the renderer must receive a visible, supplied, " +
                "circulating and direction-resolved path.");
        }

        private static void Assert(bool condition, string message)
        {
            _assertions++;
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private sealed class GraphFixture
        {
            private const string SystemKey = "test-system";
            private readonly Dictionary<string, GameMepElementData> _elements =
                new Dictionary<string, GameMepElementData>(StringComparer.Ordinal);

            public GraphFixture()
            {
                Graph.Systems.Add(new GameMepSystemData
                {
                    Key = SystemKey,
                    Name = "Test system"
                });
            }

            public GameMepGraphData Graph { get; } = new GameMepGraphData();

            public void AddElement(
                string key,
                int connectorCount,
                bool source = false,
                bool valve = false)
            {
                var element = new GameMepElementData
                {
                    Key = key,
                    Name = key,
                    SystemKey = SystemKey,
                    SystemName = "Test system"
                };
                for (int index = 0; index < connectorCount; index++)
                {
                    int graphIndex = Graph.Connectors.Count;
                    Graph.Connectors.Add(new GameMepConnectorData
                    {
                        Index = graphIndex,
                        Key = key + "|" + index,
                        ElementKey = key,
                        SystemKey = SystemKey
                    });
                    element.ConnectorIndices.Add(graphIndex);
                }

                for (int first = 0; first < connectorCount; first++)
                {
                    for (int second = first + 1; second < connectorCount; second++)
                    {
                        Graph.Connections.Add(new GameMepConnectionData
                        {
                            ConnectorA = element.ConnectorIndices[first],
                            ConnectorB = element.ConnectorIndices[second],
                            IsInternal = true,
                            IsValveGateCandidate = valve,
                            ElementKey = key
                        });
                    }
                }

                Graph.Elements.Add(element);
                _elements.Add(key, element);
                if (source)
                {
                    Graph.Sources.Add(new GameMepSourceData
                    {
                        ElementKey = key,
                        SystemKey = SystemKey,
                        Name = key,
                        IsActive = true
                    });
                }
                if (valve)
                {
                    Graph.Valves.Add(new GameMepValveData
                    {
                        ElementKey = key,
                        Kind = GameMepFlowControlKind.IsolationValve,
                        IsEnabledAsValve = true,
                        Confidence = GameMepConfidence.High,
                        EntryConnectorIndex = -1,
                        ExitConnectorIndex = -1
                    });
                }
                if (connectorCount == 2)
                {
                    element.Paths.Add(new GameMepPathData
                    {
                        ElementKey = key,
                        SystemKey = SystemKey,
                        StartConnector = element.ConnectorIndices[0],
                        EndConnector = element.ConnectorIndices[1]
                    });
                }
                Graph.RebuildIndexes();
            }

            public void Connect(string first, int firstPort, string second, int secondPort)
            {
                int firstConnector = _elements[first].ConnectorIndices[firstPort];
                int secondConnector = _elements[second].ConnectorIndices[secondPort];
                Graph.Connections.Add(new GameMepConnectionData
                {
                    ConnectorA = firstConnector,
                    ConnectorB = secondConnector
                });
                Graph.Connectors[firstConnector].IsConnected = true;
                Graph.Connectors[secondConnector].IsConnected = true;
            }

            public void SetPortDiameter(string key, int port, double diameter)
            {
                GameMepElementData element = _elements[key];
                int connector = element.ConnectorIndices[port];
                Graph.Connectors[connector].CrossSectionArea =
                    Math.PI * diameter * diameter * 0.25;
            }

            public void SetPortDirection(
                string key,
                int port,
                double x,
                double y,
                double z)
            {
                GameMepElementData element = _elements[key];
                int connector = element.ConnectorIndices[port];
                var direction = new Vector3D(x, y, z);
                direction.Normalize();
                Graph.Connectors[connector].Direction = direction;
                Graph.Connectors[connector].HasDirection = true;
            }

            public void SetPipeDiameter(string key, double diameter)
            {
                GameMepElementData element = _elements[key];
                element.IsPipeCurve = true;
                foreach (int connector in element.ConnectorIndices)
                {
                    Graph.Connectors[connector].CrossSectionArea =
                        Math.PI * diameter * diameter * 0.25;
                }
            }

            public void MarkPipeFitting(string key)
            {
                _elements[key].IsPipeFitting = true;
            }

            public void SetElementCategory(string key, string category)
            {
                _elements[key].Category = category;
            }

            public void SetElementClassification(string key, string classification)
            {
                _elements[key].Classification = classification;
            }

            public void SetElementIdentity(
                string key,
                string name,
                string typeName)
            {
                _elements[key].Name = name;
                _elements[key].TypeName = typeName;
            }

            public void SetConnectorFlowDirection(
                string key,
                int port,
                string direction)
            {
                GameMepElementData element = _elements[key];
                Graph.Connectors[element.ConnectorIndices[port]].FlowDirection =
                    direction;
            }

            public void AddJunctionPaths(string key)
            {
                GameMepElementData element = _elements[key];
                if (element.ConnectorIndices.Count < 3)
                    throw new InvalidOperationException("A junction needs at least three ports.");
                element.IsPipeJunction = true;
                foreach (int connector in element.ConnectorIndices)
                {
                    element.Paths.Add(new GameMepPathData
                    {
                        ElementKey = key,
                        SystemKey = element.SystemKey,
                        StartConnector = connector,
                        EndConnector = -1
                    });
                }
            }

            public void AddNativeTapPath(
                string key,
                int startPort,
                int endPort,
                double length)
            {
                GameMepElementData element = _elements[key];
                element.IsPipeCurve = true;
                element.IsPipeJunction = true;
                var path = new GameMepPathData
                {
                    ElementKey = key,
                    SystemKey = element.SystemKey,
                    StartConnector = element.ConnectorIndices[startPort],
                    EndConnector = element.ConnectorIndices[endPort],
                    IsVisible = true
                };
                path.Points.Add(new Point3D(0.0, 0.0, 0.0));
                path.Points.Add(new Point3D(length, 0.0, 0.0));
                path.FinalizePath();
                element.Paths.Add(path);
                element.IsVisible = true;
            }

            public void CloseValve(string key)
            {
                Graph.FindValve(key)!.IsClosed = true;
            }

            public void AddFlowControlCandidate(
                string key,
                GameMepConfidence confidence,
                bool enabled)
            {
                Graph.Valves.Add(new GameMepValveData
                {
                    ElementKey = key,
                    Kind = GameMepFlowControlKind.IsolationValve,
                    Confidence = confidence,
                    IsEnabledAsValve = enabled,
                    InitiallyEnabledAsValve = enabled,
                    DetectionReason = "test candidate"
                });
                Graph.RebuildIndexes();
            }

            public void SetConnectorSystem(
                string key,
                int port,
                string systemKey)
            {
                GameMepElementData element = _elements[key];
                Graph.Connectors[element.ConnectorIndices[port]].SystemKey = systemKey;
            }

            public void AddSystem(
                string key,
                string abbreviation,
                string classification)
            {
                Graph.Systems.Add(new GameMepSystemData
                {
                    Key = key,
                    Name = key,
                    Abbreviation = abbreviation,
                    Classification = classification
                });
                Graph.RebuildIndexes();
            }

            public void SetElementSystem(string key, string systemKey)
            {
                GameMepElementData element = _elements[key];
                element.SystemKey = systemKey;
                foreach (int connector in element.ConnectorIndices)
                    Graph.Connectors[connector].SystemKey = systemKey;
                foreach (GameMepPathData path in element.Paths)
                    path.SystemKey = systemKey;
                foreach (GameMepSourceData source in Graph.Sources.Where(item =>
                    string.Equals(item.ElementKey, key, StringComparison.Ordinal)))
                {
                    source.SystemKey = systemKey;
                }
            }

            public void SetPathLength(string key, double length)
            {
                GameMepPathData path = Path(key);
                GameMepElementData element = _elements[key];
                path.Points.Clear();
                path.Points.Add(new Point3D(0.0, 0.0, 0.0));
                path.Points.Add(new Point3D(length, 0.0, 0.0));
                path.FinalizePath();
                path.IsVisible = true;
                element.IsVisible = true;
                element.IsPipeCurve = true;
            }

            public void SetDirectedSource(
                string key,
                int entryPort,
                int exitPort)
            {
                GameMepElementData element = _elements[key];
                GameMepSourceData source = Graph.Sources.FirstOrDefault(candidate =>
                    string.Equals(candidate.ElementKey, key, StringComparison.Ordinal));
                if (source == null)
                {
                    source = new GameMepSourceData
                    {
                        ElementKey = key,
                        SystemKey = SystemKey,
                        Name = key,
                        IsActive = true
                    };
                    Graph.Sources.Add(source);
                }
                source.EntryConnectorIndex = element.ConnectorIndices[entryPort];
                source.ExitConnectorIndex = element.ConnectorIndices[exitPort];
                source.BoundaryKind = GameMepBoundaryKind.Inlet;
            }

            public void AddBoundary(string key, GameMepBoundaryKind kind)
            {
                GameMepElementData element = _elements[key];
                Graph.Sources.Add(new GameMepSourceData
                {
                    ElementKey = key,
                    SystemKey = element.SystemKey,
                    Name = key,
                    IsActive = true,
                    IsUserCreated = true,
                    BoundaryKind = kind
                });
            }

        public void SetDirectionConstraint(
            string key,
            int entryPort,
            int exitPort,
            GameMepDirectionConstraintScope scope =
                GameMepDirectionConstraintScope.LocalOverride)
        {
            GameMepElementData element = _elements[key];
            Graph.DirectionConstraints.Add(new GameMepDirectionConstraintData
            {
                ElementKey = key,
                Scope = scope,
                EntryConnectorIndex = element.ConnectorIndices[entryPort],
                ExitConnectorIndex = element.ConnectorIndices[exitPort],
                IsActive = true
            });
        }

        public void RemoveLocalDirectionConstraint(string key)
        {
            GameMepDirectionConstraintData? constraint =
                Graph.DirectionConstraints.FirstOrDefault(candidate =>
                    candidate.ElementKey == key &&
                    candidate.Scope == GameMepDirectionConstraintScope.LocalOverride);
            if (constraint != null)
                Graph.DirectionConstraints.Remove(constraint);
        }

            public GameMepPathData Path(string key)
            {
                return _elements[key].Paths.Single();
            }

            public GameMepPathData JunctionPath(string key, int port)
            {
                GameMepElementData element = _elements[key];
                int connector = element.ConnectorIndices[port];
                return element.Paths.Single(path =>
                    path.StartConnector == connector && path.EndConnector < 0);
            }

            public void Calculate()
            {
                Graph.RebuildIndexes();
                new GameMepSimulationEngine(Graph).Recalculate();
            }
        }

        private sealed class PersistenceFixture
        {
            private const string SystemKey = "persistent-test-system";
            private readonly Dictionary<string, GameMepElementData> _elements =
                new Dictionary<string, GameMepElementData>(StringComparer.Ordinal);

            public PersistenceFixture(
                string modelKey,
                string title = "Persistence test",
                bool canPersist = true)
            {
                Graph.ScenarioModelKey = modelKey;
                Graph.ScenarioCanPersist = canPersist;
                Graph.DocumentTitle = title;
                Graph.Systems.Add(new GameMepSystemData
                {
                    Key = SystemKey,
                    Name = "Persistent test system"
                });
            }

            public GameMepGraphData Graph { get; } = new GameMepGraphData();

            public void AddElement(
                string runtimeKey,
                string persistentId,
                params string[] connectorPersistentKeys)
            {
                var element = new GameMepElementData
                {
                    Key = runtimeKey,
                    PersistentId = persistentId,
                    Name = runtimeKey,
                    SystemKey = SystemKey
                };
                foreach (string connectorPersistentKey in connectorPersistentKeys)
                {
                    int index = Graph.Connectors.Count;
                    Graph.Connectors.Add(new GameMepConnectorData
                    {
                        Index = index,
                        Key = runtimeKey + "|" + index,
                        PersistentKey = connectorPersistentKey,
                        ElementKey = runtimeKey,
                        SystemKey = SystemKey
                    });
                    element.ConnectorIndices.Add(index);
                }
                Graph.Elements.Add(element);
                _elements.Add(runtimeKey, element);
                Graph.RebuildIndexes();
            }

            public void AddSource(
                string runtimeKey,
                bool active,
                bool userCreated,
                int entryPort,
                int exitPort,
                bool initiallyActive = false,
                GameMepBoundaryKind boundaryKind = GameMepBoundaryKind.Inlet)
            {
                GameMepElementData element = _elements[runtimeKey];
                Graph.Sources.Add(new GameMepSourceData
                {
                    ElementKey = runtimeKey,
                    SystemKey = SystemKey,
                    Name = runtimeKey,
                    IsActive = active,
                    InitiallyActive = initiallyActive,
                    IsUserCreated = userCreated,
                    WasManuallyOverridden = true,
                    BoundaryKind = boundaryKind,
                    EntryConnectorIndex = entryPort >= 0
                        ? element.ConnectorIndices[entryPort]
                        : -1,
                    ExitConnectorIndex = exitPort >= 0
                        ? element.ConnectorIndices[exitPort]
                        : -1
                });
            }

            public void AddDirectionConstraint(
                string runtimeKey,
                int entryPort,
                int exitPort,
                GameMepDirectionConstraintScope scope =
                    GameMepDirectionConstraintScope.EquipmentPressureRise)
            {
                GameMepElementData element = _elements[runtimeKey];
                Graph.DirectionConstraints.Add(new GameMepDirectionConstraintData
                {
                    ElementKey = runtimeKey,
                    Scope = scope,
                    EntryConnectorIndex = element.ConnectorIndices[entryPort],
                    ExitConnectorIndex = element.ConnectorIndices[exitPort],
                    IsActive = true,
                    WasManuallyOverridden = true
                });
            }

            public void AddValve(
                string runtimeKey,
                bool enabled,
                bool closed,
                bool initiallyEnabled)
            {
                Graph.Valves.Add(new GameMepValveData
                {
                    ElementKey = runtimeKey,
                    Kind = GameMepFlowControlKind.IsolationValve,
                    IsEnabledAsValve = enabled,
                    InitiallyEnabledAsValve = initiallyEnabled,
                    IsClosed = closed,
                    WasManuallyOverridden = true,
                    Confidence = GameMepConfidence.High,
                    EntryConnectorIndex = -1,
                    ExitConnectorIndex = -1
                });
                Graph.RebuildIndexes();
            }
        }
    }
}
