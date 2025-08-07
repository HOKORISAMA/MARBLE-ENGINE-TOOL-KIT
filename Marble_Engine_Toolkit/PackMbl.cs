using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MarbleEngineTools
{
    public class MblPacker
    {
        public void Pack(string folderPath, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                throw new ArgumentNullException(nameof(folderPath));
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            Console.WriteLine($"Packing folder: {folderPath}");
            Console.WriteLine($"Output archive: {outputPath}");

            var metadata = ReadMetadata(folderPath);
            var (version, filenameLength, keyBytes) = InitializeFromMetadata(metadata);
            var files = GetOrderedFiles(folderPath, metadata);

            if (version == "v3")
            {
                filenameLength = CalculateFilenameLength(files);
            }

            var entries = PrepareEntries(files, version, filenameLength);
            WriteArchive(outputPath, entries, files, version, filenameLength, keyBytes);

            Console.WriteLine($"Successfully packed {files.Count} files into {Path.GetFileName(outputPath)}");
        }

        private JsonElement ReadMetadata(string folderPath)
        {
            string metadataPath = Path.Combine(folderPath, "index.json");
            if (!File.Exists(metadataPath))
                throw new FileNotFoundException($"Metadata file not found: {metadataPath}");

            string jsonContent = File.ReadAllText(metadataPath);
            return JsonSerializer.Deserialize<JsonElement>(jsonContent);
        }

        private (string version, uint filenameLength, byte[] keyBytes) InitializeFromMetadata(JsonElement metadata)
        {
            string version = "v1";
            if (metadata.TryGetProperty("Version", out JsonElement versionElement))
            {
                version = versionElement.GetString()?.ToLowerInvariant() ?? "v1";
            }

            uint filenameLength = version switch
            {
                "v1" => 0x10,
                "v2" => 0x38,
                "v3" => 0, // Will be calculated later
                _ => throw new ArgumentException($"Invalid version '{version}'")
            };

            byte[] keyBytes = null;
            if (metadata.TryGetProperty("Key", out JsonElement keyElement))
            {
                string keyHex = keyElement.GetString();
                if (!string.IsNullOrEmpty(keyHex))
                {
                    keyBytes = Utility.ConvertHexStringToBytes(keyHex);
                    Console.WriteLine("Encryption key loaded from metadata.");
                }
            }

            Console.WriteLine($"Archive format: {version.ToUpper()}");
            return (version, filenameLength, keyBytes);
        }

        private List<FileInfo> GetOrderedFiles(string folderPath, JsonElement metadata)
        {
            var orderedFilenames = new List<string>();

            // Handle both formats: array of strings (old) or array of objects (new)
            if (metadata.TryGetProperty("Files", out JsonElement filesElement))
            {
                foreach (JsonElement fileElement in filesElement.EnumerateArray())
                {
                    string filename = null;
                    
                    if (fileElement.ValueKind == JsonValueKind.String)
                    {
                        // Old format: just filename strings
                        filename = fileElement.GetString();
                    }
                    else if (fileElement.ValueKind == JsonValueKind.Object)
                    {
                        // New format: objects with Name property
                        if (fileElement.TryGetProperty("Name", out JsonElement nameElement))
                        {
                            filename = nameElement.GetString();
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(filename))
                    {
                        orderedFilenames.Add(filename);
                    }
                }
            }

            if (!orderedFilenames.Any())
                throw new InvalidOperationException("No files defined in metadata");

            var orderedFiles = new List<FileInfo>();
            foreach (var filename in orderedFilenames)
            {
                var fullPath = Path.Combine(folderPath, filename);
                var fileInfo = new FileInfo(fullPath);

                if (!fileInfo.Exists)
                    throw new FileNotFoundException($"File specified in metadata not found: {filename}");

                orderedFiles.Add(fileInfo);
            }

            return orderedFiles;
        }

        private uint CalculateFilenameLength(List<FileInfo> files)
        {
            return files.Select(f => (uint)GetModifiedFileName(f.Name).Length).Max();
        }

        private string GetModifiedFileName(string fileName)
        {
			string result;
			if (fileName.EndsWith(".s", StringComparison.OrdinalIgnoreCase))
			{
				// Replace .s extension with \x00S
				result = Path.GetFileNameWithoutExtension(fileName) + "\x00S";
			}
			else
			{
				// Replace the '.' before the extension with '\x00'
				int dotIndex = fileName.LastIndexOf('.');
				if (dotIndex >= 0)
				{
					result = fileName.Substring(0, dotIndex) + "\x00" + fileName.Substring(dotIndex + 1);
				}
				else
				{
					// No extension — leave unchanged
					result = fileName;
				}
			}
            
            // Capitalize the filename
            return result.ToUpperInvariant();
        }

        private List<Entry> PrepareEntries(List<FileInfo> files, string version, uint filenameLength)
        {
            var entries = new List<Entry>();
            
            uint headerSize = version == "v3" ? 8u : 4u; // v3 has file count + filename length, v1/v2 just have file count
            uint indexSize = (uint)(files.Count * (filenameLength + 8));
            uint paddingSize = version == "v3" ? 0u : 4u; // v1/v2 have 4 null bytes after header
            uint currentOffset = headerSize + indexSize + paddingSize;

            // Validate that we won't exceed reasonable file size limits
            long totalSize = currentOffset;
            foreach (var file in files)
            {
                totalSize += file.Length;
                if (totalSize > int.MaxValue)
                    throw new InvalidOperationException("Archive would exceed maximum supported size");
            }

            foreach (var file in files)
            {
                string modifiedName = GetModifiedFileName(file.Name);
                
                // Validate filename length
                if (version != "v3" && modifiedName.Length >= filenameLength)
                {
                    throw new InvalidOperationException(
                        $"Filename '{modifiedName}' is too long for format {version} (max {filenameLength - 1} characters)");
                }

                entries.Add(new Entry
                {
                    Name = modifiedName,
                    Offset = (int)currentOffset,
                    Size = (int)file.Length,
                    OriginalPath = file.FullName
                });

                currentOffset += (uint)file.Length;
            }

            return entries;
        }

        private void WriteArchive(string outputPath, List<Entry> entries, List<FileInfo> files, 
            string version, uint filenameLength, byte[] keyBytes)
        {
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            // Write header based on version
            bw.Write((uint)files.Count);
            if (version == "v3")
            {
                bw.Write(filenameLength);
            }

            // Write index
            foreach (var entry in entries)
            {
                Utility.WriteFixedLengthString(bw, entry.Name, (int)filenameLength);
                bw.Write((uint)entry.Offset);
                bw.Write((uint)entry.Size);
            }

            // Write padding for v1/v2 formats (4 null bytes after entire header section)
            if (version != "v3")
            {
                bw.Write((uint)0);
            }

            // Write file data
            bool containsScripts = Path.GetFileNameWithoutExtension(outputPath)
                .EndsWith("_data", StringComparison.OrdinalIgnoreCase);

            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var entry = entries[i];

                // Validate offset before seeking
                if (entry.Offset < 0 || entry.Offset > fs.Length)
                {
                    throw new InvalidOperationException($"Invalid offset calculated for file {file.Name}: {entry.Offset}");
                }

                fs.Seek(entry.Offset, SeekOrigin.Begin);
                
                byte[] data = File.ReadAllBytes(file.FullName);
                
                // Verify data size matches expected
                if (data.Length != entry.Size)
                {
                    throw new InvalidOperationException(
                        $"File size mismatch for {file.Name}: expected {entry.Size}, got {data.Length}");
                }
                
                bool isScript = containsScripts || 
                    file.Name.EndsWith(".s", StringComparison.OrdinalIgnoreCase);
                
                if (isScript && keyBytes != null)
                {
                    data = Xor.XorEncrypt(data, keyBytes);
                }

                bw.Write(data);
                Console.WriteLine($"Packed: {file.Name} ({data.Length} bytes)");
            }
        }
    }
}
