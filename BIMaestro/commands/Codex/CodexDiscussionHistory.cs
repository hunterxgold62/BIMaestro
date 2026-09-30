using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BIMaestro.Codex
{
    internal sealed class CodexDiscussion
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; }
        public string Document { get; set; }
        public bool Mep { get; set; }
        public int Provider { get; set; }
        public string Model { get; set; }
        public string Effort { get; set; }
        public bool Internet { get; set; }
        public string SessionId { get; set; }
        public string Transcript { get; set; }
        public string Draft { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<string> Families { get; set; } = new List<string>();
        [JsonIgnore] public string Label => UpdatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm") +
            " · " + (Provider == 1 ? "Claude" : "Codex") + " · " + Title + " · " + Document;
    }

    internal sealed class CodexDiscussionHistory
    {
        private readonly string directory;
        internal CodexDiscussionHistory(string directory = null)
        {
            this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIMaestro", "Discussions");
        }
        private string FilePath(string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Identifiant de discussion invalide.");
            return Path.Combine(directory, id + ".json");
        }
        internal IDisposable Acquire(string id)
        {
            Directory.CreateDirectory(directory);
            return new FileStream(FilePath(id) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        internal void Save(CodexDiscussion discussion)
        {
            Directory.CreateDirectory(directory);
            string path = FilePath(discussion.Id);
            string temporary = path + ".tmp";
            discussion.UpdatedUtc = DateTime.UtcNow;
            File.WriteAllText(temporary, JsonConvert.SerializeObject(discussion), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        internal CodexDiscussion Read(string id)
        {
            var result = JsonConvert.DeserializeObject<CodexDiscussion>(File.ReadAllText(FilePath(id)));
            if (result == null || result.Id != id || result.Families == null || result.Provider < 0 || result.Provider > 1)
                throw new InvalidDataException("Discussion endommagée.");
            return result;
        }
        internal List<CodexDiscussion> List(bool mep, out int unreadable)
        {
            unreadable = 0;
            var result = new List<CodexDiscussion>();
            if (!Directory.Exists(directory)) return result;
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try { var item = Read(Path.GetFileNameWithoutExtension(path)); if (item.Mep == mep) result.Add(item); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) { unreadable++; }
            }
            return result.OrderByDescending(item => item.UpdatedUtc).ToList();
        }
    }
}
