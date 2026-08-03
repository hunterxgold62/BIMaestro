using System;
using System.Collections.Generic;
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
                NetworkWithoutSourceStaysUnknown();
                EmptyGraphDoesNotFail();
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

        private static void EmptyGraphDoesNotFail()
        {
            var graph = new GameMepGraphData();
            new GameMepSimulationEngine(graph).Recalculate();
            Assert(graph.LastCalculationMilliseconds >= 0.0,
                "An empty Revit model must produce an empty, valid graph.");
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
            }

            public void Calculate()
            {
                Graph.RebuildIndexes();
                new GameMepSimulationEngine(Graph).Recalculate();
            }
        }
    }
}
