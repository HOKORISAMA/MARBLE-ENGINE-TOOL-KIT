using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Marble
{
    public class MblPacker
    {
        private readonly ArcOpen _arcFile;
        private readonly string _outputPath;
        private readonly IList<Entry> _entries;
        private byte[] _key;
        private string _version;
        private uint _filenameLength;

        public MblPacker(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            _outputPath = outputPath;
            _arcFile = new ArcOpen(_outputPath, FileMode.Create);
            _entries = new List<Entry>();
            _key = ArcEncoding.Shift_JIS.GetBytes(""); // Default key
        }

        public void Pack(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                throw new ArgumentNullException(nameof(folderPath));

            var metadata = ReadMetadata(folderPath);
            InitializeVersion(metadata);

            var files = GetOrderedFiles(folderPath, metadata);

            if (_version == "v3")
            {
                _filenameLength = CalculateFilenameLength(files);
            }

            PreparePacking(files);
            WriteArchive(files);
        }

        private JsonElement ReadMetadata(string folderPath)
        {
            string metadataPath = Path.Combine(folderPath, "index.json");
            if (!File.Exists(metadataPath))
                throw new FileNotFoundException("Metadata file not found", metadataPath);

            string jsonContent = File.ReadAllText(metadataPath);
            return JsonSerializer.Deserialize<JsonElement>(jsonContent);
        }

        private void InitializeVersion(JsonElement metadata)
        {
            try
            {
                string version = "v1";

                if (metadata.TryGetProperty("Version", out JsonElement versionElement))
                {
                    version = versionElement.GetString()?.ToLowerInvariant() ?? "v1";
                }

                _version = version;
                _filenameLength = _version switch
                {
                    "v1" => 0x10,
                    "v2" => 0x38,
                    "v3" => 0,
                    _ => throw new ArgumentException($"Invalid version '{_version}'")
                };

                // Read key from metadata
                if (metadata.TryGetProperty("Key", out JsonElement keyElement))
                {
                    string keyHex = keyElement.GetString();
                    if (!string.IsNullOrEmpty(keyHex))
                    {
                        _key = Utility.ConvertHexStringToBytes(keyHex);
                        Console.WriteLine("Key successfully read from metadata.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing metadata: {ex}");
            }
        }

        private List<FileInfo> GetOrderedFiles(string folderPath, JsonElement metadata)
        {
            var orderedFilenames = metadata.GetProperty("Files").EnumerateArray()
                .Select(f => f.GetString())
                .Where(f => f != null)
                .ToList();

            if (!orderedFilenames.Any())
                throw new InvalidOperationException("No files defined in metadata");

            var orderedFiles = new List<FileInfo>();
            foreach (var filename in orderedFilenames)
            {
                var fullPath = Path.Combine(folderPath, filename);
                var fileInfo = new FileInfo(fullPath);

                if (!fileInfo.Exists)
                {
                    throw new FileNotFoundException($"File specified in metadata not found: {filename}");
                }

                orderedFiles.Add(fileInfo);
            }

            return orderedFiles;
        }

        private uint CalculateFilenameLength(List<FileInfo> files)
        {
            return files.Select(f => (uint)(GetModifiedFileName(f.Name).Length)).Max();
        }

        private string GetModifiedFileName(string fileName)
        {
            if (fileName.EndsWith(".s", StringComparison.OrdinalIgnoreCase))
            {
                return fileName.Substring(0, fileName.Length - 2) + "\0S";
            }
            return fileName;
        }

        private void PreparePacking(List<FileInfo> files)
        {
            _entries.Clear();

            uint headerSize = _version == "v1" || _version == "v2" ? 4u + 4u : 8u;
            uint indexSize = (uint)(files.Count * (_filenameLength + 8));
            uint currentOffset = headerSize + indexSize;

            foreach (var file in files)
            {
                _entries.Add(new Entry
                {
                    Name = GetModifiedFileName(file.Name).ToUpperInvariant(),
                    Offset = (int)currentOffset,
                    Size = (int)file.Length
                });

                currentOffset += (uint)file.Length;
            }
        }

        private void WriteArchive(List<FileInfo> files)
        {
            long totalSize = CalculateTotalSize(files);
            _arcFile.SetLength(totalSize);

            _arcFile.Seek(0);
            _arcFile.WriteInt32(files.Count);

            if (_version != "v1" && _version != "v2")
            {
                _arcFile.WriteUInt32(_filenameLength);
            }

            foreach (var entry in _entries)
            {
                _arcFile.WriteFixedLengthString(entry.Name, (int)_filenameLength);
                _arcFile.WriteInt32(entry.Offset);
                _arcFile.WriteInt32(entry.Size);
            }

            foreach (var file in files)
            {
                var entry = _entries.First(e => e.Name == GetModifiedFileName(file.Name).ToUpperInvariant());
                _arcFile.Seek(entry.Offset);

                byte[] data = File.ReadAllBytes(file.FullName);
                bool isScript = file.Name.EndsWith(".s", StringComparison.OrdinalIgnoreCase) ||
                               file.Directory?.Name.EndsWith("_data", StringComparison.OrdinalIgnoreCase) == true;

                if (isScript)
                {
                    data = Xor.XorEncrypt(data, _key);
                }

                _arcFile.Write(data);
            }
        }

        private long CalculateTotalSize(List<FileInfo> files)
        {
            long size = _version == "v1" || _version == "v2" ? 4 : 8;
            size += files.Count * (_filenameLength + 8);

            foreach (var file in files)
            {
                size += file.Length;
            }

            return size;
        }
    }
}
