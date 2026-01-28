using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace BIMaestro.RibbonLayout
{
    public class RibbonPanelDefinition
    {
        public RibbonPanelDefinition(string name, IEnumerable<RibbonItemDefinition> items)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Items = new List<RibbonItemDefinition>(items ?? Array.Empty<RibbonItemDefinition>());
        }

        public string Name { get; }
        public List<RibbonItemDefinition> Items { get; }
    }

    public class RibbonItemDefinition
    {
        public RibbonItemDefinition(string id, string displayName, Action<RibbonPanel> builder)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            DisplayName = displayName ?? id;
            Builder = builder ?? throw new ArgumentNullException(nameof(builder));
        }

        public string Id { get; }
        public string DisplayName { get; }
        public Action<RibbonPanel> Builder { get; }
    }
}