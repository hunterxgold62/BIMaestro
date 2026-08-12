using System.Collections.Generic;

namespace Page
{
    public sealed class ButtonUpdateNote
    {
        public string Version { get; set; }
        public string ButtonId { get; set; }
        public string CommandClass { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public List<string> Changes { get; set; } = new List<string>();
        public string EnglishTitle { get; set; }
        public string EnglishSummary { get; set; }
        public List<string> EnglishChanges { get; set; } = new List<string>();
    }
}
