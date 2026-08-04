using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
                MissingDirectionsDoNotBreakReachability();
                DirectedPipeSourceSuppliesOnlyChosenSide();
                ArrivalAndReturnStabilizeDirection();
                EqualOpposingArrivalsStayAmbiguous();
                ParallelBypassesKeepTheSameDirection();
                PumpConstraintDoesNotCreateSupply();
                NetworkWithoutSourceStaysUnknown();
                EmptyGraphDoesNotFail();
                ScenarioRoundTripRestoresSourcesAndValves();
                ResetRemovesPersistedScenario();
                ChangedNetworkSkipsInvalidDirection();
                ScenarioFilesAreIsolatedByModel();
                UnsavedModelPersistsOnlyInCurrentSession();
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
            fixture.SetDirectionConstraint("pump", 0, 1);

            fixture.Calculate();

            AssertState(fixture, "pump", GameMepFlowState.Unknown);
            Assert(fixture.Path("pump").DirectionState == GameMepDirectionState.Resolved &&
                fixture.Path("pump").FlowForward,
                "A pump may impose direction without being treated as a fluid source.");
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
                Assert(result.RestoredValves == 2, "Two valve states must be restored.");
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
                        IsEnabledAsValve = true,
                        Confidence = GameMepConfidence.High
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
                Graph.Connections.Add(new GameMepConnectionData
                {
                    ConnectorA = _elements[first].ConnectorIndices[firstPort],
                    ConnectorB = _elements[second].ConnectorIndices[secondPort]
                });
            }

            public void CloseValve(string key)
            {
                Graph.FindValve(key)!.IsClosed = true;
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

            public void SetDirectionConstraint(string key, int entryPort, int exitPort)
            {
                GameMepElementData element = _elements[key];
                Graph.DirectionConstraints.Add(new GameMepDirectionConstraintData
                {
                    ElementKey = key,
                    EntryConnectorIndex = element.ConnectorIndices[entryPort],
                    ExitConnectorIndex = element.ConnectorIndices[exitPort],
                    IsActive = true
                });
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
                int exitPort)
            {
                GameMepElementData element = _elements[runtimeKey];
                Graph.DirectionConstraints.Add(new GameMepDirectionConstraintData
                {
                    ElementKey = runtimeKey,
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
                    IsEnabledAsValve = enabled,
                    InitiallyEnabledAsValve = initiallyEnabled,
                    IsClosed = closed,
                    WasManuallyOverridden = true,
                    Confidence = GameMepConfidence.High
                });
                Graph.RebuildIndexes();
            }
        }
    }
}
