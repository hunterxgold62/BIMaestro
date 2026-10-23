namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        public sealed class WallJoinEnd
        {
            public int End { get; set; }
            public bool JoinAllowed { get; set; }
            public string JoinType { get; set; }
            public string[] Participants { get; set; }
        }
    }
}
