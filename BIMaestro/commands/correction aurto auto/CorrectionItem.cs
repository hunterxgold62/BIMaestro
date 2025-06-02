namespace ScanTextRevit
{
    /// <summary>
    /// Représente une correction retournée par l'IA.
    /// </summary>
    public class CorrectionItem
    {
        /// <summary>
        /// Numéro de ligne dans l'entrée (défini par l'IA).
        /// </summary>
        public int LineNumber { get; set; }
        public string OriginalText { get; set; }
        public string CorrectedText { get; set; }
        public string Explanation { get; set; }
        /// <summary>
        /// "Mineur" si la correction concerne uniquement la ponctuation ou les espaces, 
        /// "Erreur" si c'est une véritable faute.
        /// </summary>
        public string Category { get; set; }
        /// <summary>
        /// Identifiant de l'élément Revit source.
        /// </summary>
        public string ElementId { get; set; }
    }
}
