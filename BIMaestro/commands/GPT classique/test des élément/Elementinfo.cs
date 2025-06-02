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
        public string SurfaceAndVolume { get; set; }

        public override string ToString()
        {
            return $"**Id**: {Id}\n" +
                   $"**Nom**: {Name}\n" +
                   $"**Catégorie**: {Category}\n" +
                   $"**Matériau**: {Material}\n" +
                   $"**Niveau**: {Level}\n" +
                   $"**Dimension**:\n{CustomParameters}";
        }
    }
}
