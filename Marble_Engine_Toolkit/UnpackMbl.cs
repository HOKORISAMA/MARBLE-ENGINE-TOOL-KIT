using System.Text;
using System.Text.Json;

namespace Marble
{
    public class MblOpener
    {
        private readonly ArcOpen _arcFile;
        private readonly IList<Entry> _entries;
        private readonly byte[] _key;
        private string _version;  // Added to store version

        public MblOpener(string filepath, string key)
        {
            _arcFile = new ArcOpen(filepath ?? throw new ArgumentNullException(nameof(filepath)));
            _entries = new List<Entry>();
            _key = ArcEncoding.Shift_JIS.GetBytes(key); //ArcEncoding.Shift_JIS.GetBytes("女教師ゆうこ1968");
            _version = "unknown";
        }

        private bool IsValidPlacement(long offset, long size, long maxOffset) =>
            offset >= 0 && size >= 0 && (offset + size) <= maxOffset;

        private IList<Entry> ReadIndex(uint filenameLen, uint indexOffset, int count)
        {
            if (count <= 0 || filenameLen == 0)
                return null;

            uint indexSize = (8u + filenameLen) * (uint)count;
            if (indexSize > _arcFile.FileSize - indexOffset)
                return null;

            var directory = new List<Entry>(count);
            long currentOffset = indexOffset;

            for (int i = 0; i < count; i++)
            {
                var entry = ReadEntry(ref currentOffset, filenameLen, indexSize);
                if (entry == null)
                    return null;

                directory.Add(entry);
            }

            return directory.Count > 0 ? directory : null;
        }

        private Entry ReadEntry(ref long currentOffset, uint filenameLen, uint indexSize)
        {
            string name = _arcFile.ReadString(currentOffset, (int)filenameLen);
            if (string.IsNullOrEmpty(name))
                return null;

            name = ProcessFileName(name, filenameLen, currentOffset);
            currentOffset += filenameLen;

            uint offset = (uint)_arcFile.ReadInt32(currentOffset);
            uint size = (uint)_arcFile.ReadInt32(currentOffset + 4);

            if (offset < indexSize || !IsValidPlacement(offset, size, _arcFile.FileSize))
                return null;

            currentOffset += 8;

            return new Entry
            {
                Name = name.ToLowerInvariant(),
                Offset = (int)offset,
                Size = (int)size
            };
        }

        private string ProcessFileName(string name, uint filenameLen, long currentOffset)
        {
            if (filenameLen - name.Length <= 1)
                return name;

            string ext = _arcFile.ReadString(
                currentOffset + name.Length + 1,
                (int)(filenameLen - name.Length - 1)
            );

            return string.IsNullOrEmpty(ext) ? name : Path.ChangeExtension(name, ext);
        }

        public bool Extract()
        {
            try
            {
                int count = _arcFile.ReadInt32(0);
                if (!_arcFile.IsSaneCount(count))
                    return false;

                uint filenameLen = (uint)_arcFile.ReadInt32(4);
                
                if (filenameLen > 0 && filenameLen <= 0xFF)
                {
                    var entries = ReadIndex(filenameLen, 8, count);
                    if (entries != null)
                    {
                        _entries.Clear();
                        foreach (var entry in entries)
                            _entries.Add(entry);
                        _version = "v3";  // Variable length index
                        return true;
                    }
                }

                var tempEntries = ReadIndex(0x10, 4, count);
                if (tempEntries != null)
                {
                    _entries.Clear();
                    foreach (var entry in tempEntries)
                        _entries.Add(entry);
                    _version = "v1";  // 0x10 index
                    return true;
                }

                tempEntries = ReadIndex(0x38, 4, count);
                if (tempEntries != null)
                {
                    _entries.Clear();
                    foreach (var entry in tempEntries)
                        _entries.Add(entry);
                    _version = "v2";  // 0x38 index
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading archive: {ex.Message}");
                return false;
            }
        }

        public void SaveFiles(string outputDir)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
                throw new ArgumentNullException(nameof(outputDir));

            Directory.CreateDirectory(outputDir);

            if (_entries.Count == 0)
            {
                Console.WriteLine("No entries to extract.");
                return;
            }

            bool containsScripts = Path.GetFileNameWithoutExtension(_arcFile.Filename)
                .EndsWith("_data", StringComparison.OrdinalIgnoreCase);

            var metadata = new
            {
                Key = BitConverter.ToString(_key).Replace("-", ""),
                Version = _version,
                Files = _entries.Select(e => e.Name).ToList()
            };

            foreach (var entry in _entries)
            {
                ExtractEntry(entry, outputDir, containsScripts);
            }

            // Write metadata to a single JSON file
            string metadataPath = Path.Combine(outputDir, "index.json");
            File.WriteAllText(metadataPath, System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }));

            Console.WriteLine($"Metadata saved to {metadataPath}");
        }

        private void ExtractEntry(Entry entry, string outputDir, bool containsScripts)
        {
            try
            {
                byte[] data = new byte[entry.Size];
                _arcFile.Read((uint)entry.Offset, data, 0, entry.Size);

                bool isScript = containsScripts || 
                    entry.Name.EndsWith(".s", StringComparison.OrdinalIgnoreCase);
                
                if (isScript)
                {
                    data = Xor.XorDecrypt(data, _key);
                }

                string outputPath = Path.Combine(outputDir, entry.Name);
                string directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllBytes(outputPath, data);
                Console.WriteLine($"Extracted {entry.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error extracting {entry.Name}: {ex.Message}");
            }
        }
    }
}
