// ElementInfo.cs
using System.Text;

namespace IA
{
    public class ElementInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Material { get; set; }
        public string CustomParameters { get; set; }
        public string Level { get; set; }
        public object SurfaceAndVolume { get; internal set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**Id**: {Id}");
            sb.AppendLine($"**Nom**: {Name}");
            sb.AppendLine($"**Catégorie**: {Category}");
            sb.AppendLine($"**Matériau**: {Material}");
            sb.AppendLine($"**Niveau**: {Level}");
            sb.AppendLine($"**Paramètres dimensionnels**:\n{CustomParameters}");
            return sb.ToString();
        }
    }
}
