namespace Analyse
{
    internal static class CollaborativeModelTrackerStore
    {
        internal static string ActiveDirectory => System.IO.Path.GetDirectoryName(typeof(CollaborativeModelTrackerStore).Assembly.Location);
    }
}
