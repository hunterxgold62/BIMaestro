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

        private static int Main()
        {
            try
            {
                StraightValveCutsOnlyPath();
                TeeSuppliesBothBranches();
                LoopBypassesClosedValve();
                SecondSourceMaintainsSupply();
                DisconnectedBranchIsIsolated();
                ThreeWayValveCutsEveryOutlet();
                CheckValveAllowsForwardFlow();
                CheckValveBlocksReverseFlow();
                ReversedCheckValveAllowsTheOppositeDirection();
                MissingDirectionsDoNotBreakReachability();
                DirectedPipeSourceSuppliesOnlyChosenSide();
                ArrivalAndReturnStabilizeDirection();
                ClosedValveDeadEndStaysPressurizedWithoutCirculation();
                UnmarkedOpenEndKeepsCirculation();
                EqualOpposingArrivalsStayAmbiguous();
                ParallelBypassesKeepTheSameDirection();
                PumpConstraintDoesNotCreateSupply();
                ManualDirectionOverrideBeatsAutomaticPotential();
                LocalDirectionOverrideDoesNotReorientNeighbors();
                DirectionExplanationNamesSourceAndReturn();
                DirectionExplanationListsAlternativeSourcesDeterministically();
                DirectionExplanationReportsAlternativeLoop();
                DirectionExplanationReportsCheckValveLimit();
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
                NetworkTraceRespectsCheckValveDirection();
                NetworkTraceHonorsHiddenSystems();
                NetworkTraceStaysFastOnLargeGraph();
                DiagnosticsClassifyCriticalDirectionConflict();
                DiagnosticsDetectCheckValveWithoutDirection();
                DiagnosticsDetectAmbiguousFlowControl();
                DiagnosticsDetectUnknownPassThroughComponent();
                DiagnosticsDetectDisconnectedElement();
                DiagnosticsGroupOpenConnectorsInSmartMode();
                DiagnosticsDetectBranchWithoutSource();
                DiagnosticsDetectIncompatibleSystems();
                DiagnosticsReportInvalidSavedSettings();
                NetworkWithoutSourceStaysUnknown();
                EmptyGraphDoesNotFail();
                ScenarioRoundTripRestoresSourcesAndValves();
                ResetRemovesPersistedScenario();
                ChangedNetworkSkipsInvalidDirection();
                ScenarioFilesAreIsolatedByModel();
                UnsavedModelPersistsOnlyInCurrentSession();
                SelectionIndexChoosesNearestPreciseTriangle();
                SelectionIndexRedirectsInsulationToPipe();
                SelectionIndexPrefersPreciseGeometryToBoundsFallback();
                SelectionIndexStaysWithinHoverBudget();
                Console.WriteLine("MEP graph regression tests: " + _assertions + " assertions passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                return 1;
            }
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

        private static void CheckValveAllowsForwardFlow()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("check", 2, checkValve: true);
            fixture.AddElement("terminal", 1);
            fixture.Connect("source", 0, "check", 0);
            fixture.Connect("check", 1, "terminal", 0);

            fixture.Calculate();

            AssertState(fixture, "terminal", GameMepFlowState.Supplied);
        }

        private static void CheckValveBlocksReverseFlow()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("terminal", 1);
            fixture.AddElement("check", 2, checkValve: true);
            fixture.AddElement("source", 1, source: true);
            fixture.Connect("terminal", 0, "check", 0);
            fixture.Connect("check", 1, "source", 0);

            fixture.Calculate();

            AssertState(fixture, "terminal", GameMepFlowState.Isolated);
        }

        private static void ReversedCheckValveAllowsTheOppositeDirection()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("terminal", 1);
            fixture.AddElement("check", 2, checkValve: true);
            fixture.AddElement("source", 1, source: true);
            fixture.Connect("terminal", 0, "check", 0);
            fixture.Connect("check", 1, "source", 0);
            fixture.ReverseCheckValve("check");

            fixture.Calculate();

            AssertState(fixture, "terminal", GameMepFlowState.Supplied);
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

        private static void DirectionExplanationReportsCheckValveLimit()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("check", 2, checkValve: true);
            fixture.AddElement("pipe", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("source", 0, "check", 0);
            fixture.Connect("check", 1, "pipe", 0);
            fixture.Connect("pipe", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);

            fixture.Calculate();

            Assert(fixture.Path("pipe").DirectionExplanation.LimitingControls
                .Any(item => item.IndexOf("Clapet", StringComparison.OrdinalIgnoreCase) >= 0),
                "A check valve crossed by the upstream path must be named.");
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
            fixture.AddElement("check", 2, checkValve: true);
            fixture.AddElement("pump", 2);
            fixture.SetDirectedSource("boundary", 0, 1);
            GameMepSourceData arrival = fixture.Graph.Sources.First(item =>
                item.ElementKey == "arrival");
            GameMepSourceData boundary = fixture.Graph.Sources.First(item =>
                item.ElementKey == "boundary");
            GameMepValveData valve = fixture.Graph.FindValve("valve")!;
            GameMepValveData check = fixture.Graph.FindValve("check")!;
            int originalCheckEntry = check.EntryConnectorIndex;
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
            Assert(history.Execute(fixture.Graph, "check reversed", () =>
            {
                int entry = check.EntryConnectorIndex;
                check.EntryConnectorIndex = check.ExitConnectorIndex;
                check.ExitConnectorIndex = entry;
                check.WasManuallyOverridden = true;
            }), "A check-valve reversal must be recorded.");
            Assert(history.Execute(fixture.Graph, "check ignored",
                () => check.IsEnabledAsValve = false),
                "A check-valve classification change must be recorded.");
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

            Assert(history.UndoCount == 8 && !history.CanRedo,
                "Every functional mutation must create one command.");
            for (int index = 0; index < 8; index++)
                Assert(history.TryUndo(fixture.Graph, false, out _),
                    "Every command in the chain must be undoable.");
            arrival = fixture.Graph.Sources.First(item => item.ElementKey == "arrival");
            boundary = fixture.Graph.Sources.First(item => item.ElementKey == "boundary");
            Assert(arrival.IsActive && boundary.EntryConnectorIndex <
                boundary.ExitConnectorIndex,
                "Undo must restore source activation and direction.");
            Assert(!valve.IsClosed && check.IsEnabledAsValve &&
                check.EntryConnectorIndex == originalCheckEntry,
                "Undo must restore valve and check-valve states.");
            Assert(fixture.Graph.DirectionConstraints.Count == 0 &&
                fixture.Graph.Sources.All(item => item.Name != "manual source"),
                "Undo must remove added constraints and sources.");

            for (int index = 0; index < 8; index++)
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

        private static void NetworkTraceRespectsCheckValveDirection()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("check", 2, checkValve: true);
            fixture.AddElement("pipe", 2);
            fixture.AddElement("return", 1);
            fixture.Connect("source", 0, "check", 0);
            fixture.Connect("check", 1, "pipe", 0);
            fixture.Connect("pipe", 1, "return", 0);
            fixture.AddBoundary("return", GameMepBoundaryKind.Outlet);
            fixture.Calculate();
            Assert(GameMepNetworkTracer.Build(
                    fixture.Graph, "source", GameMepTraceMode.Downstream)
                .ElementKeys.Contains("pipe"),
                "A forward check valve must allow downstream tracing.");

            fixture.ReverseCheckValve("check");
            fixture.Calculate();
            Assert(!GameMepNetworkTracer.Build(
                    fixture.Graph, "source", GameMepTraceMode.Downstream)
                .ElementKeys.Contains("pipe"),
                "A reversed check valve must stop downstream tracing.");
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

        private static void DiagnosticsDetectCheckValveWithoutDirection()
        {
            GraphFixture fixture = new GraphFixture();
            fixture.AddElement("source", 1, source: true);
            fixture.AddElement("check", 2, checkValve: true);
            fixture.ClearCheckValveDirection("check");
            fixture.Connect("source", 0, "check", 0);

            fixture.Calculate();

            Assert(fixture.Graph.Diagnostics.Any(item =>
                    item.Kind == GameMepDiagnosticKind.CheckValveDirectionMissing &&
                    item.Severity == GameMepDiagnosticSeverity.Warning &&
                    item.ElementKey == "check"),
                "An undefined check-valve orientation must be a localized warning, not an incorrect-direction claim.");
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
                saved.AddElement("check-old", "uid-check", "check-in", "check-out");
                saved.AddElement("pump-old", "uid-pump", "pump-in", "pump-out");
                saved.AddSource("source-a-old", true, true, 0, 1);
                saved.AddSource("source-b-old", true, true, 1, 0,
                    boundaryKind: GameMepBoundaryKind.Outlet);
                saved.AddSource("automatic-old", false, false, -1, -1, initiallyActive: true);
                saved.AddValve("valve-a-old", true, true, initiallyEnabled: true);
                saved.AddValve("valve-b-old", false, false, initiallyEnabled: true);
                saved.AddValve("check-old", true, false, true,
                    GameMepFlowControlKind.CheckValve, 1, 0);
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
                restored.AddElement("check-new", "uid-check", "check-in", "check-out");
                restored.AddElement("pump-new", "uid-pump", "pump-in", "pump-out");
                restored.AddSource("automatic-new", true, false, -1, -1, initiallyActive: true);
                restored.AddValve("valve-a-new", true, false, initiallyEnabled: true);
                restored.AddValve("valve-b-new", true, false, initiallyEnabled: true);
                restored.AddValve("check-new", true, false, true,
                    GameMepFlowControlKind.CheckValve, 0, 1);

                GameMepScenarioRestoreResult result =
                    GameMepScenarioStore.Restore(restored.Graph, directory);
                Assert(result.Error.Length == 0, "A valid scenario must restore without error.");
                Assert(result.RestoredSources == 3, "Three source states must be restored.");
                Assert(result.RestoredValves == 3,
                    "Two valves and one check valve must be restored.");
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
                GameMepValveData restoredCheck = restored.Graph.FindValve("check-new")!;
                Assert(restoredCheck.Kind == GameMepFlowControlKind.CheckValve &&
                    restored.Graph.Connectors[restoredCheck.EntryConnectorIndex]
                        .PersistentKey == "check-out" &&
                    restored.Graph.Connectors[restoredCheck.ExitConnectorIndex]
                        .PersistentKey == "check-in",
                    "The corrected check-valve direction must survive connector reindexing.");
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
                bool valve = false,
                bool checkValve = false)
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
                            IsValveGateCandidate = valve || checkValve,
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
                if (valve || checkValve)
                {
                    Graph.Valves.Add(new GameMepValveData
                    {
                        ElementKey = key,
                        Kind = checkValve
                            ? GameMepFlowControlKind.CheckValve
                            : GameMepFlowControlKind.IsolationValve,
                        IsEnabledAsValve = true,
                        Confidence = GameMepConfidence.High,
                        EntryConnectorIndex = checkValve
                            ? element.ConnectorIndices[0]
                            : -1,
                        ExitConnectorIndex = checkValve
                            ? element.ConnectorIndices[1]
                            : -1
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

            public void CloseValve(string key)
            {
                Graph.FindValve(key)!.IsClosed = true;
            }

            public void ReverseCheckValve(string key)
            {
                GameMepValveData checkValve = Graph.FindValve(key)!;
                int entry = checkValve.EntryConnectorIndex;
                checkValve.EntryConnectorIndex = checkValve.ExitConnectorIndex;
                checkValve.ExitConnectorIndex = entry;
            }

            public void ClearCheckValveDirection(string key)
            {
                GameMepValveData checkValve = Graph.FindValve(key)!;
                checkValve.EntryConnectorIndex = -1;
                checkValve.ExitConnectorIndex = -1;
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
                Graph.Sources.Add(new GameMepSourceData
                {
                    ElementKey = key,
                    SystemKey = SystemKey,
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
                bool initiallyEnabled,
                GameMepFlowControlKind kind =
                    GameMepFlowControlKind.IsolationValve,
                int entryPort = -1,
                int exitPort = -1)
            {
                GameMepElementData element = _elements[runtimeKey];
                Graph.Valves.Add(new GameMepValveData
                {
                    ElementKey = runtimeKey,
                    Kind = kind,
                    IsEnabledAsValve = enabled,
                    InitiallyEnabledAsValve = initiallyEnabled,
                    IsClosed = closed,
                    WasManuallyOverridden = true,
                    Confidence = GameMepConfidence.High,
                    EntryConnectorIndex = entryPort >= 0
                        ? element.ConnectorIndices[entryPort]
                        : -1,
                    ExitConnectorIndex = exitPort >= 0
                        ? element.ConnectorIndices[exitPort]
                        : -1
                });
                Graph.RebuildIndexes();
            }
        }
    }
}
