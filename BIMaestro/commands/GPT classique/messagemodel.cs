using System.Windows.Documents;
using System.Diagnostics;
using Markdown.Xaml;
using System;

namespace IA
{
    public class MessageModel
    {
        public string Role { get; set; }    // "user", "assistant", ou "system"
        public string Content { get; set; }

        // Propriété pour le FlowDocument formaté
        public FlowDocument FlowDocumentContent
        {
            get
            {
                try
                {
                    var markdown = new Markdown.Xaml.Markdown();
                    return markdown.Transform(Content);
                }
                catch (Exception ex)
                {
                    // En cas d'erreur, journaliser l'exception et retourner un FlowDocument avec le texte brut
                    Debug.WriteLine($"Erreur lors de la transformation du Markdown : {ex.Message}");

                    FlowDocument doc = new FlowDocument();
                    doc.Blocks.Add(new Paragraph(new Run(Content)));
                    return doc;
                }
            }
        }
    }
}
