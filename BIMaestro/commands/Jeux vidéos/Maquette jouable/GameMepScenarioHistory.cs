using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.VideoGames
{
    internal static class GameMepScenarioReset
    {
        public static void ResetValvesToInitial(
            IEnumerable<GameMepValveData> valves)
        {
            foreach (GameMepValveData valve in valves.ToList())
            {
                valve.IsClosed = false;
                valve.IsEnabledAsValve = valve.InitiallyEnabledAsValve;
                valve.EntryConnectorIndex = valve.InitiallyEntryConnectorIndex;
                valve.ExitConnectorIndex = valve.InitiallyExitConnectorIndex;
                valve.WasManuallyOverridden = false;
            }
        }

        public static void ResetSourcesAndDirections(
            GameMepGraphData graph,
            Func<GameMepElementData, bool> includeElement)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (includeElement == null)
                throw new ArgumentNullException(nameof(includeElement));
            foreach (GameMepSourceData source in graph.Sources.ToList())
            {
                GameMepElementData? element = graph.FindElement(source.ElementKey);
                if (element == null || !includeElement(element))
                    continue;
                if (source.IsUserCreated)
                {
                    graph.Sources.Remove(source);
                }
                else
                {
                    source.IsActive = source.InitiallyActive;
                    source.EntryConnectorIndex = -1;
                    source.ExitConnectorIndex = -1;
                    source.WasManuallyOverridden = false;
                }
            }
            foreach (GameMepDirectionConstraintData constraint in
                graph.DirectionConstraints.ToList())
            {
                GameMepElementData? element =
                    graph.FindElement(constraint.ElementKey);
                if (element != null && includeElement(element))
                    graph.DirectionConstraints.Remove(constraint);
            }
        }

        public static bool ElementBelongsToSystem(
            GameMepGraphData graph,
            GameMepElementData element,
            string systemKey)
        {
            if (graph == null || element == null ||
                string.IsNullOrWhiteSpace(systemKey))
            {
                return false;
            }
            if (string.Equals(element.SystemKey, systemKey, StringComparison.Ordinal))
                return true;
            return element.ConnectorIndices.Any(index =>
                index >= 0 && index < graph.Connectors.Count &&
                string.Equals(
                    graph.Connectors[index].SystemKey,
                    systemKey,
                    StringComparison.Ordinal));
        }
    }

    internal sealed class GameMepScenarioHistory
    {
        private const int MaximumCommandCount = 100;
        private readonly List<GameMepScenarioCommand> _undo =
            new List<GameMepScenarioCommand>();
        private readonly List<GameMepScenarioCommand> _redo =
            new List<GameMepScenarioCommand>();

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public int UndoCount => _undo.Count;
        public int RedoCount => _redo.Count;
        public string UndoLabel => CanUndo ? _undo[_undo.Count - 1].Label : string.Empty;
        public string RedoLabel => CanRedo ? _redo[_redo.Count - 1].Label : string.Empty;

        public bool Execute(
            GameMepGraphData graph,
            string label,
            Action mutation,
            bool calculationInProgress = false)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            if (calculationInProgress)
                return false;

            GameMepScenarioMemoryState before =
                GameMepScenarioMemoryState.Capture(graph);
            mutation();
            GameMepScenarioMemoryState after =
                GameMepScenarioMemoryState.Capture(graph);
            if (before.IsEquivalentTo(after))
                return false;

            _undo.Add(new GameMepScenarioCommand(
                string.IsNullOrWhiteSpace(label) ? "Modification MEP" : label,
                before,
                after));
            if (_undo.Count > MaximumCommandCount)
                _undo.RemoveAt(0);
            _redo.Clear();
            return true;
        }

        public bool TryUndo(
            GameMepGraphData graph,
            bool calculationInProgress,
            out string label)
        {
            label = string.Empty;
            if (calculationInProgress || _undo.Count == 0)
                return false;
            GameMepScenarioCommand command = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            command.Before.ApplyTo(graph);
            _redo.Add(command);
            label = command.Label;
            return true;
        }

        public bool TryRedo(
            GameMepGraphData graph,
            bool calculationInProgress,
            out string label)
        {
            label = string.Empty;
            if (calculationInProgress || _redo.Count == 0)
                return false;
            GameMepScenarioCommand command = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            command.After.ApplyTo(graph);
            _undo.Add(command);
            label = command.Label;
            return true;
        }
    }

    internal sealed class GameMepScenarioCommand
    {
        public GameMepScenarioCommand(
            string label,
            GameMepScenarioMemoryState before,
            GameMepScenarioMemoryState after)
        {
            Label = label ?? string.Empty;
            Before = before ?? throw new ArgumentNullException(nameof(before));
            After = after ?? throw new ArgumentNullException(nameof(after));
        }

        public string Label { get; }
        public GameMepScenarioMemoryState Before { get; }
        public GameMepScenarioMemoryState After { get; }
    }

    internal sealed class GameMepScenarioMemoryState
    {
        public IList<GameMepValveMemoryState> Valves { get; } =
            new List<GameMepValveMemoryState>();
        public IList<GameMepSourceMemoryState> Sources { get; } =
            new List<GameMepSourceMemoryState>();
        public IList<GameMepDirectionConstraintMemoryState> DirectionConstraints
            { get; } = new List<GameMepDirectionConstraintMemoryState>();

        public static GameMepScenarioMemoryState Capture(GameMepGraphData graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            var state = new GameMepScenarioMemoryState();
            foreach (GameMepValveData valve in graph.Valves)
            {
                state.Valves.Add(new GameMepValveMemoryState
                {
                    ElementKey = valve.ElementKey,
                    Kind = valve.Kind,
                    Confidence = valve.Confidence,
                    DetectionReason = valve.DetectionReason,
                    IsEnabledAsValve = valve.IsEnabledAsValve,
                    IsClosed = valve.IsClosed,
                    WasManuallyOverridden = valve.WasManuallyOverridden,
                    EntryConnectorIndex = valve.EntryConnectorIndex,
                    ExitConnectorIndex = valve.ExitConnectorIndex
                });
            }
            foreach (GameMepSourceData source in graph.Sources)
            {
                state.Sources.Add(new GameMepSourceMemoryState
                {
                    ElementKey = source.ElementKey,
                    SystemKey = source.SystemKey,
                    Name = source.Name,
                    Confidence = source.Confidence,
                    IsActive = source.IsActive,
                    InitiallyActive = source.InitiallyActive,
                    WasManuallyOverridden = source.WasManuallyOverridden,
                    IsUserCreated = source.IsUserCreated,
                    BoundaryKind = source.BoundaryKind,
                    EntryConnectorIndex = source.EntryConnectorIndex,
                    ExitConnectorIndex = source.ExitConnectorIndex
                });
            }
            foreach (GameMepDirectionConstraintData constraint in
                graph.DirectionConstraints)
            {
                state.DirectionConstraints.Add(
                    new GameMepDirectionConstraintMemoryState
                    {
                        ElementKey = constraint.ElementKey,
                        Scope = constraint.Scope,
                        EntryConnectorIndex = constraint.EntryConnectorIndex,
                        ExitConnectorIndex = constraint.ExitConnectorIndex,
                        IsActive = constraint.IsActive,
                        WasManuallyOverridden = constraint.WasManuallyOverridden
                    });
            }
            return state;
        }

        public void ApplyTo(GameMepGraphData graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            var valveStates = Valves
                .GroupBy(item => item.ElementKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(),
                    StringComparer.Ordinal);
            foreach (GameMepValveData valve in graph.Valves)
            {
                if (!valveStates.TryGetValue(valve.ElementKey, out
                        GameMepValveMemoryState state))
                {
                    continue;
                }
                valve.Kind = state.Kind;
                valve.Confidence = state.Confidence;
                valve.DetectionReason = state.DetectionReason;
                valve.IsEnabledAsValve = state.IsEnabledAsValve;
                valve.IsClosed = state.IsClosed;
                valve.WasManuallyOverridden = state.WasManuallyOverridden;
                valve.EntryConnectorIndex = state.EntryConnectorIndex;
                valve.ExitConnectorIndex = state.ExitConnectorIndex;
            }

            graph.Sources.Clear();
            foreach (GameMepSourceMemoryState state in Sources)
            {
                graph.Sources.Add(new GameMepSourceData
                {
                    ElementKey = state.ElementKey,
                    SystemKey = state.SystemKey,
                    Name = state.Name,
                    Confidence = state.Confidence,
                    IsActive = state.IsActive,
                    InitiallyActive = state.InitiallyActive,
                    WasManuallyOverridden = state.WasManuallyOverridden,
                    IsUserCreated = state.IsUserCreated,
                    BoundaryKind = state.BoundaryKind,
                    EntryConnectorIndex = state.EntryConnectorIndex,
                    ExitConnectorIndex = state.ExitConnectorIndex
                });
            }

            graph.DirectionConstraints.Clear();
            foreach (GameMepDirectionConstraintMemoryState state in
                DirectionConstraints)
            {
                graph.DirectionConstraints.Add(
                    new GameMepDirectionConstraintData
                    {
                        ElementKey = state.ElementKey,
                        Scope = state.Scope,
                        EntryConnectorIndex = state.EntryConnectorIndex,
                        ExitConnectorIndex = state.ExitConnectorIndex,
                        IsActive = state.IsActive,
                        WasManuallyOverridden = state.WasManuallyOverridden
                    });
            }
            graph.RebuildIndexes();
        }

        public bool IsEquivalentTo(GameMepScenarioMemoryState other)
        {
            if (other == null || Valves.Count != other.Valves.Count ||
                Sources.Count != other.Sources.Count ||
                DirectionConstraints.Count != other.DirectionConstraints.Count)
            {
                return false;
            }
            return Valves.Zip(other.Valves, (first, second) =>
                    first.IsEquivalentTo(second)).All(value => value) &&
                Sources.Zip(other.Sources, (first, second) =>
                    first.IsEquivalentTo(second)).All(value => value) &&
                DirectionConstraints.Zip(other.DirectionConstraints,
                    (first, second) => first.IsEquivalentTo(second))
                    .All(value => value);
        }
    }

    internal sealed class GameMepValveMemoryState
    {
        public string ElementKey { get; set; } = string.Empty;
        public GameMepFlowControlKind Kind { get; set; }
        public GameMepConfidence Confidence { get; set; }
        public string DetectionReason { get; set; } = string.Empty;
        public bool IsEnabledAsValve { get; set; }
        public bool IsClosed { get; set; }
        public bool WasManuallyOverridden { get; set; }
        public int EntryConnectorIndex { get; set; }
        public int ExitConnectorIndex { get; set; }

        public bool IsEquivalentTo(GameMepValveMemoryState other)
        {
            return other != null && ElementKey == other.ElementKey &&
                Kind == other.Kind && Confidence == other.Confidence &&
                DetectionReason == other.DetectionReason &&
                IsEnabledAsValve == other.IsEnabledAsValve &&
                IsClosed == other.IsClosed &&
                WasManuallyOverridden == other.WasManuallyOverridden &&
                EntryConnectorIndex == other.EntryConnectorIndex &&
                ExitConnectorIndex == other.ExitConnectorIndex;
        }
    }

    internal sealed class GameMepSourceMemoryState
    {
        public string ElementKey { get; set; } = string.Empty;
        public string SystemKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public GameMepConfidence Confidence { get; set; }
        public bool IsActive { get; set; }
        public bool InitiallyActive { get; set; }
        public bool WasManuallyOverridden { get; set; }
        public bool IsUserCreated { get; set; }
        public GameMepBoundaryKind BoundaryKind { get; set; }
        public int EntryConnectorIndex { get; set; }
        public int ExitConnectorIndex { get; set; }

        public bool IsEquivalentTo(GameMepSourceMemoryState other)
        {
            return other != null && ElementKey == other.ElementKey &&
                SystemKey == other.SystemKey && Name == other.Name &&
                Confidence == other.Confidence && IsActive == other.IsActive &&
                InitiallyActive == other.InitiallyActive &&
                WasManuallyOverridden == other.WasManuallyOverridden &&
                IsUserCreated == other.IsUserCreated &&
                BoundaryKind == other.BoundaryKind &&
                EntryConnectorIndex == other.EntryConnectorIndex &&
                ExitConnectorIndex == other.ExitConnectorIndex;
        }
    }

    internal sealed class GameMepDirectionConstraintMemoryState
    {
        public string ElementKey { get; set; } = string.Empty;
        public GameMepDirectionConstraintScope Scope { get; set; }
        public int EntryConnectorIndex { get; set; }
        public int ExitConnectorIndex { get; set; }
        public bool IsActive { get; set; }
        public bool WasManuallyOverridden { get; set; }

        public bool IsEquivalentTo(
            GameMepDirectionConstraintMemoryState other)
        {
            return other != null && ElementKey == other.ElementKey &&
                Scope == other.Scope &&
                EntryConnectorIndex == other.EntryConnectorIndex &&
                ExitConnectorIndex == other.ExitConnectorIndex &&
                IsActive == other.IsActive &&
                WasManuallyOverridden == other.WasManuallyOverridden;
        }
    }
}
