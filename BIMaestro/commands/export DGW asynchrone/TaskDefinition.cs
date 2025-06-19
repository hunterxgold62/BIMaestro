using System.Collections.Generic;

namespace MonPluginRevit
{
    public class TaskDefinition
    {
        public List<int> Views { get; set; } = new List<int>();
        public string ExportDir { get; set; }
        public ExportOptionsDefinition Options { get; set; }
    }

    public class ExportOptionsDefinition
    {
        public bool MergedViews { get; set; }
        public string TargetUnit { get; set; }
        public string Colors { get; set; }
    }
}
