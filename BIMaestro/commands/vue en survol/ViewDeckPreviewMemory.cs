using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BIMaestro.ViewHover
{
    // Lifetime follows the native layout model, not the optional decoration.
    // OFF only restores the UI. Weak keys release closed/native-replaced tabs.
    internal sealed class ViewDeckPreviewMemory<TDocument> where TDocument : class
    {
        internal sealed class Entry
        {
            internal TDocument Document { get; private set; }
            internal string ViewUniqueId { get; private set; }
            internal string PreviewPath { get; private set; }
            internal ViewDeckCachedImage Preview { get; private set; } = new ViewDeckCachedImage();

            internal void Remember(TDocument document, string viewUniqueId, string path)
            {
                if (!EqualityComparer<TDocument>.Default.Equals(Document, document) || ViewUniqueId != viewUniqueId)
                    Preview = new ViewDeckCachedImage(); // Never reuse another view's pixels.
                Document = document;
                ViewUniqueId = viewUniqueId;
                PreviewPath = path;
            }

            internal void Clear()
            {
                Document = null;
                ViewUniqueId = null;
                PreviewPath = null;
                Preview = new ViewDeckCachedImage();
            }
        }

        private ConditionalWeakTable<object, Entry> _entries = new ConditionalWeakTable<object, Entry>();
        internal Entry ForModel(object model) => _entries.GetValue(model, _ => new Entry());
        internal void Clear() => _entries = new ConditionalWeakTable<object, Entry>();
    }

    internal sealed class ViewDeckCachedImage
    {
        private string _loadedPath;
        private long _loadedStamp = -1;
        private long _loadedLength = -1;
        internal ImageSource Image { get; private set; }
        internal long Revision { get; private set; }

        internal void Refresh(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) return; // Keep the last valid preview during replacement/cleanup.
                long stamp = file.LastWriteTimeUtc.Ticks;
                long length = file.Length;
                if (path == _loadedPath && stamp == _loadedStamp && length == _loadedLength) return;
                BitmapImage replacement = ReadImage(path);
                // Publish only after the entire replacement was decoded successfully.
                Image = replacement;
                _loadedPath = path;
                _loadedStamp = stamp;
                _loadedLength = length;
                Revision++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is NotSupportedException || ex is FormatException || ex is ArgumentException)
            {
                // An unavailable/partial PNG must not erase a valid previous image.
                // Do not remember the failed stamp: retry when the file becomes readable.
            }
        }

        private static BitmapImage ReadImage(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                // Keep the PNG's native resolution for the large hover preview.
                // Existing smaller images stay usable until naturally refreshed.
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        // The exporter produces a unique PNG beside the cache target. Validate
        // it first, then atomically replace the old file; never truncate it.
        internal static void PublishGeneratedImage(string generated, string target)
        {
            ReadImage(generated);
            if (File.Exists(target)) File.Replace(generated, target, null);
            else File.Move(generated, target);
        }
    }
}
