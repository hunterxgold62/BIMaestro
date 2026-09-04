namespace BIMaestro.UI
{
    public sealed class RadialItem
    {
        public string FamilyPath { get; set; }
        public string ImagePath { get; set; }
        public string Label { get; set; }
        public string ButtonId { get; set; }
        public string CommandClass { get; set; }
        public bool IsPinned { get; set; }

        public bool HasFamily => !string.IsNullOrWhiteSpace(FamilyPath);
        public bool HasAction => HasFamily
            || !string.IsNullOrWhiteSpace(ButtonId)
            || !string.IsNullOrWhiteSpace(CommandClass);
    }
}
