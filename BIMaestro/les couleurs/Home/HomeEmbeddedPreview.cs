using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BIMaestro.Home
{
    /// <summary>Reads only bounded CFB allocation/directory/preview sectors. Never opens a Revit document.</summary>
    internal static class HomeEmbeddedPreview
    {
        internal static byte[] TryRead(string localPath) { return TryReadPng(localPath); }
        internal static string TryGetDataUrl(string localPath)
        {
            var bytes = TryReadPng(localPath);
            return bytes == null ? null : "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        internal static byte[] TryReadPng(string localPath)
        {
            try
            {
                // Network reads are deliberately excluded: a synchronous file read cannot enforce a wall-clock timeout.
                if (string.IsNullOrWhiteSpace(localPath) || !Path.IsPathRooted(localPath) || localPath.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                string extension = Path.GetExtension(localPath);
                if (!string.Equals(extension, ".rvt", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".rfa", StringComparison.OrdinalIgnoreCase)) return null;
                var drive = new DriveInfo(Path.GetPathRoot(localPath));
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) return null;
                using (var reader = new Reader(localPath)) return reader.ReadPreview();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is InvalidDataException || ex is OverflowException || ex is IndexOutOfRangeException)
            { return null; }
        }
        sealed class Entry { internal string Name; internal byte Type; internal uint Start; internal ulong Size; }
        sealed class Reader : IDisposable
        {
            const uint End = 0xfffffffe, Free = 0xffffffff;
            const int MaxRead = 12 * 1024 * 1024, MaxAllocation = 1024 * 1024, MaxDirectory = 2 * 1024 * 1024, MaxPreview = 4 * 1024 * 1024;
            readonly FileStream file;
            readonly Stopwatch clock = Stopwatch.StartNew();
            int totalRead, sectorSize;
            uint[] fat;
            public Reader(string path) { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess); }
            public void Dispose() { file.Dispose(); }
            void Check() { if (clock.ElapsedMilliseconds > 350) throw new InvalidDataException("Preview budget exceeded"); }
            byte[] Read(long position, int count)
            {
                Check();
                if (count < 0 || count > MaxPreview || position < 0 || position > file.Length - count || totalRead > MaxRead - count) throw new InvalidDataException("Read budget or bounds");
                var output = new byte[count]; file.Position = position; int offset = 0;
                while (offset < count) { Check(); int got = file.Read(output, offset, count - offset); if (got == 0) throw new EndOfStreamException(); offset += got; }
                totalRead += count; return output;
            }
            byte[] Sector(uint id) { if (id >= End) throw new InvalidDataException("Special sector"); return Read(checked(((long)id + 1) * sectorSize), sectorSize); }
            static uint U32(byte[] data, int at) { return BitConverter.ToUInt32(data, at); }
            static ushort U16(byte[] data, int at) { return BitConverter.ToUInt16(data, at); }
            uint Next(uint id, uint[] table) { if (id >= table.Length) throw new InvalidDataException("Allocation index"); return table[id]; }
            byte[] Chain(uint start, uint[] table, int bytes, bool exact)
            {
                if (bytes < 0 || bytes > MaxPreview) throw new InvalidDataException("Chain size");
                var output = new MemoryStream(); var seen = new HashSet<uint>(); uint id = start;
                while (id != End && output.Length < bytes)
                {
                    Check(); if (id >= End || !seen.Add(id) || seen.Count > bytes / sectorSize + 1) throw new InvalidDataException("Chain cycle/bounds");
                    Next(id, table); var sector = Sector(id); int take = Math.Min(sector.Length, bytes - (int)output.Length); output.Write(sector, 0, take); id = Next(id, table);
                }
                if (exact && output.Length != bytes) throw new InvalidDataException("Truncated chain");
                return output.ToArray();
            }
            internal byte[] ReadPreview()
            {
                var h = Read(0, 512); byte[] signature = { 0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1 };
                for (int i = 0; i < 8; i++) if (h[i] != signature[i]) return null;
                int major = U16(h, 26), shift = U16(h, 30);
                if (U16(h, 28) != 0xfffe || (major != 3 && major != 4) || (shift != 9 && shift != 12) || (major == 3 && shift != 9) || (major == 4 && shift != 12) || U16(h, 32) != 6 || U32(h, 56) != 4096) return null;
                sectorSize = 1 << shift;
                uint count = U32(h, 44), difatCount = U32(h, 72), nextDifat = U32(h, 68);
                if (count == 0 || count > MaxAllocation / (sectorSize / 4) || difatCount > 128) return null;
                var fatSectors = new List<uint>(); var ids = new HashSet<uint>();
                for (int i = 0; i < 109; i++) { uint id = U32(h, 76 + i * 4); if (id != Free) { if (id >= End || !ids.Add(id)) return null; fatSectors.Add(id); } }
                var difatSeen = new HashSet<uint>();
                for (int n = 0; n < difatCount; n++)
                {
                    if (nextDifat >= End || !difatSeen.Add(nextDifat)) return null;
                    var d = Sector(nextDifat);
                    for (int i = 0; i < sectorSize / 4 - 1; i++) { uint id = U32(d, i * 4); if (id != Free) { if (id >= End || !ids.Add(id) || fatSectors.Count >= count) return null; fatSectors.Add(id); } }
                    nextDifat = U32(d, sectorSize - 4);
                }
                if (fatSectors.Count != count) return null;
                fat = new uint[checked((int)count * (sectorSize / 4))]; int dest = 0;
                foreach (uint id in fatSectors) { var data = Sector(id); for (int i = 0; i < sectorSize; i += 4) fat[dest++] = U32(data, i); }
                byte[] directory = Chain(U32(h, 48), fat, MaxDirectory, false); Entry root = null, preview = null;
                for (int pos = 0; pos + 128 <= directory.Length; pos += 128)
                {
                    Check(); int length = U16(directory, pos + 64); if (length < 2 || length > 64 || length % 2 != 0) continue;
                    var entry = new Entry { Name = Encoding.Unicode.GetString(directory, pos, length - 2), Type = directory[pos + 66], Start = U32(directory, pos + 116), Size = BitConverter.ToUInt64(directory, pos + 120) };
                    if (major == 3) entry.Size &= uint.MaxValue;
                    if (entry.Type == 5) root = entry;
                    if (entry.Type == 2 && string.Equals(entry.Name, "RevitPreview4.0", StringComparison.OrdinalIgnoreCase)) preview = entry;
                }
                if (preview == null || preview.Size < 8 || preview.Size > MaxPreview) return null;
                byte[] raw;
                if (preview.Size >= 4096) raw = Chain(preview.Start, fat, (int)preview.Size, true);
                else
                {
                    uint miniCount = U32(h, 64); if (root == null || miniCount == 0 || miniCount > 2048 || root.Size > 64 * 1024 * 1024) return null;
                    byte[] miniBytes = Chain(U32(h, 60), fat, checked((int)miniCount * sectorSize), true);
                    var mini = new uint[miniBytes.Length / 4]; for (int i = 0; i < mini.Length; i++) mini[i] = U32(miniBytes, i * 4);
                    var roots = new List<uint>(); var seen = new HashSet<uint>(); uint id = root.Start;
                    int rootCount = checked((int)((root.Size + (ulong)sectorSize - 1) / (ulong)sectorSize));
                    if (rootCount > 131072) return null;
                    while (id != End) { Check(); if (id >= End || roots.Count >= rootCount || !seen.Add(id)) return null; roots.Add(id); id = Next(id, fat); }
                    var output = new MemoryStream(); seen.Clear(); id = preview.Start;
                    while (output.Length < (long)preview.Size)
                    {
                        Check(); if (id >= End || !seen.Add(id) || seen.Count > 64) return null;
                        long offset = (long)id * 64; if (offset + 64 > (long)root.Size || offset / sectorSize >= roots.Count) return null;
                        byte[] block = Read(((long)roots[(int)(offset / sectorSize)] + 1) * sectorSize + offset % sectorSize, 64);
                        output.Write(block, 0, Math.Min(64, (int)preview.Size - (int)output.Length)); id = Next(id, mini);
                    }
                    raw = output.ToArray();
                }
                return ExtractPng(raw);
            }
            static uint Big(byte[] b, int p) { return ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3]; }
            byte[] ExtractPng(byte[] data)
            {
                byte[] magic = { 137,80,78,71,13,10,26,10 }; int start = -1;
                for (int at = 0; at <= Math.Min(4096, data.Length - 8); at++) { bool match = true; for (int i = 0; i < 8; i++) if (data[at + i] != magic[i]) { match = false; break; } if (match) { start = at; break; } }
                if (start < 0) return null;
                int pos = start + 8, chunks = 0; bool image = false;
                while (pos <= data.Length - 12 && chunks++ < 1024)
                {
                    Check(); uint length = Big(data, pos); if (length > MaxPreview || length > data.Length - pos - 12) return null;
                    string type = Encoding.ASCII.GetString(data, pos + 4, 4);
                    if (chunks == 1 && (type != "IHDR" || length != 13)) return null;
                    if (type == "IHDR") { uint w = Big(data, pos + 8), height = Big(data, pos + 12); if (w == 0 || height == 0 || w > 2048 || height > 2048) return null; }
                    uint crc = 0xffffffff;
                    for (int n = pos + 4; n < pos + 8 + (int)length; n++) { if ((n & 4095) == 0) Check(); crc ^= data[n]; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u); }
                    if ((crc ^ 0xffffffff) != Big(data, pos + 8 + (int)length)) return null;
                    if (type == "IDAT") image = true;
                    pos += checked((int)length + 12);
                    if (type == "IEND") { if (length != 0 || !image) return null; var result = new byte[pos - start]; Buffer.BlockCopy(data, start, result, 0, result.Length); return result; }
                }
                return null;
            }
        }
    }
}

