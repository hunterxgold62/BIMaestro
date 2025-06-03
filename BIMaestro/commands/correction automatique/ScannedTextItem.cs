namespace ScanTextRevit
{
    /// <summary>
    /// Contient le texte scanné et l'identifiant de l'élément source.
    /// </summary>
    public class ScannedTextItem
    {
        public string Text { get; set; }
        public string ElementId { get; set; }
    }
}