namespace BIMaestro.PropertiesAppearance
{
    internal sealed class PropertyPreviewRow
    {
        internal string GroupKey, ParameterKey, Label, Value;
        internal bool Header, ReadOnly;
        internal string OrderKey => Header ? "g:" + GroupKey : "p:" + (ParameterKey ?? GroupKey + "/native") + "/row:" + AppearancePreferences.Normalize(Label);
    }
}
