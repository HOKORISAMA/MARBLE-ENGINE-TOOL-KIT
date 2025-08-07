using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MarbleEngineTools
{
    public class MblOpener
    {
        public void Unpack(string inputPath, string outputPath, string key = null)
        {
            using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);
            byte[] keyBytes = string.IsNullOrEmpty(key) ? null : 
                ArcEncoding.Shift_JIS.GetBytes(key);
            
            Console.WriteLine($"Reading archive: {Path.GetFileName(inputPath)}");
            uint fileCount = br.ReadUInt32();
            if (!IsSaneCount(fileCount))
            {
                throw new InvalidDataException($"File count is not sane: {fileCount}");
            }
            
            Console.WriteLine($"File count: {fileCount}");
            uint filenameLen = br.ReadUInt32();
            string version = "unknown";
            List<Entry> entries = null;
            
            // Try variable length index (v3)
            if (filenameLen > 0 && filenameLen <= 0xFF)
            {
                entries = ReadIndex(br, filenameLen, 8, (int)fileCount);
                if (entries != null)
                {
                    version = "v3";
                    Console.WriteLine("Detected format: v3 (variable length index)");
                }
            }
            
            // Try v1 format (0x10 filename length)
            if (entries == null)
            {
                entries = ReadIndex(br, 0x10, 4, (int)fileCount);
                if (entries != null)
                {
                    version = "v1";
                    Console.WriteLine("Detected format: v1 (0x10 index)");
                }
            }
            
            // Try v2 format (0x38 filename length)
            if (entries == null)
            {
                entries = ReadIndex(br, 0x38, 4, (int)fileCount);
                if (entries != null)
                {
                    version = "v2";
                    Console.WriteLine("Detected format: v2 (0x38 index)");
                }
            }
            
            if (entries == null)
            {
                throw new InvalidDataException("Could not detect archive format");
            }
            
            ExtractFiles(br, entries, outputPath, keyBytes, version, inputPath);
        }
        
        private bool IsSaneCount(uint count) => count > 0 && count <= 0xFFFFFF;
        
        private List<Entry> ReadIndex(BinaryReader br, uint filenameLen, uint indexOffset, int count)
        {
            if (count <= 0 || filenameLen == 0)
                return null;
                
            uint indexSize = (8u + filenameLen) * (uint)count;
            if (indexSize > br.BaseStream.Length - indexOffset)
                return null;
                
            var entries = new List<Entry>(count);
            br.BaseStream.Seek(indexOffset, SeekOrigin.Begin);
            
            for (int i = 0; i < count; i++)
            {
                var entry = ReadEntry(br, filenameLen, indexSize);
                if (entry == null)
                    return null;
                entries.Add(entry);
            }
            
            return entries.Count > 0 ? entries : null;
        }
        
        private Entry ReadEntry(BinaryReader br, uint filenameLen, uint indexSize)
        {
            long namePosition = br.BaseStream.Position;
            string name = ReadString(br, (int)filenameLen);
            
            if (string.IsNullOrEmpty(name))
                return null;
                
            name = ProcessFileName(br, name, filenameLen, namePosition);
            
            // Skip to offset/size data
            br.BaseStream.Seek(namePosition + filenameLen, SeekOrigin.Begin);
            uint offset = br.ReadUInt32();
            uint size = br.ReadUInt32();
            
            // Validate entry placement
            if (offset < indexSize || !IsValidPlacement(offset, size, br.BaseStream.Length))
                return null;
                
            return new Entry
            {
                Name = name.ToLowerInvariant(),
                Offset = (int)offset,
                Size = (int)size
            };
        }
        
        private bool IsValidPlacement(long offset, long size, long maxOffset) =>
            offset >= 0 && size >= 0 && (offset + size) <= maxOffset;
            
        private string ProcessFileName(BinaryReader br, string name, uint filenameLen, long namePosition)
        {
            if (filenameLen - name.Length <= 1)
                return name;
                
            long extPosition = namePosition + name.Length + 1;
            int extLength = (int)(filenameLen - name.Length - 1);
            
            br.BaseStream.Seek(extPosition, SeekOrigin.Begin);
            string ext = ReadString(br, extLength);
            return string.IsNullOrEmpty(ext) ? name : Path.ChangeExtension(name, ext);
        }
        
        private string ReadString(BinaryReader br, int length)
        {
            byte[] bytes = br.ReadBytes(length);
            int nullIndex = Array.IndexOf(bytes, (byte)0);
            if (nullIndex >= 0)
            {
                Array.Resize(ref bytes, nullIndex);
            }
            return Encoding.GetEncoding("Shift_JIS").GetString(bytes);
        }
        
        private void ExtractFiles(BinaryReader br, List<Entry> entries, string outputPath, 
            byte[] keyBytes, string version, string inputPath)
        {
            Directory.CreateDirectory(outputPath);
            if (entries.Count == 0)
            {
                Console.WriteLine("No entries to extract.");
                return;
            }
            
            bool containsScripts = Path.GetFileNameWithoutExtension(inputPath)
                .EndsWith("_data", StringComparison.OrdinalIgnoreCase);
                
            // Initialize metadata file
            string metadataPath = Path.Combine(outputPath, "index.json");
            var extractedFiles = new List<string>();
            InitializeMetadata(metadataPath, version, keyBytes, entries.Count);
            
            Console.WriteLine($"Extracting {entries.Count} files...");
            
            int extractedCount = 0;
            foreach (var entry in entries)
            {
                bool success = ExtractEntry(br, entry, outputPath, keyBytes, containsScripts);
                if (success)
                {
                    extractedFiles.Add(entry.Name);
                    extractedCount++;
                    
                    // Update metadata after each successful extraction
                    UpdateMetadata(metadataPath, version, keyBytes, entries.Count, 
                                 extractedFiles, extractedCount);
                }
            }
            
            Console.WriteLine($"Extraction complete. {extractedCount}/{entries.Count} files extracted.");
        }
        
        private bool ExtractEntry(BinaryReader br, Entry entry, string outputPath, 
            byte[] keyBytes, bool containsScripts)
        {
            try
            {
                br.BaseStream.Seek(entry.Offset, SeekOrigin.Begin);
                byte[] data = br.ReadBytes(entry.Size);
                
                // Decrypt if it's a script file and we have a key
                bool isScript = containsScripts || entry.Name.EndsWith(".s", StringComparison.OrdinalIgnoreCase);
                if (isScript && keyBytes != null)
                {
                    data = Xor.XorDecrypt(data, keyBytes);
                }
                
                string filePath = Path.Combine(outputPath, entry.Name);
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                    
                File.WriteAllBytes(filePath, data);
                Console.WriteLine($"Extracted: {entry.Name} ({entry.Size} bytes)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error extracting {entry.Name}: {ex.Message}");
                return false;
            }
        }
        
        private void InitializeMetadata(string metadataPath, string version, byte[] keyBytes, int totalFiles)
        {
            try
            {
                var metadata = new
                {
                    Version = version,
                    Key = keyBytes != null ? BitConverter.ToString(keyBytes).Replace("-", "") : null,
                    TotalFileCount = totalFiles,
                    ExtractedFileCount = 0,
                    ExtractionStatus = "In Progress",
                    LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Files = new List<string>()
                };
                
                string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                
                File.WriteAllText(metadataPath, json);
                Console.WriteLine($"Metadata initialized at: {metadataPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not initialize metadata: {ex.Message}");
            }
        }
        
        private void UpdateMetadata(string metadataPath, string version, byte[] keyBytes, 
            int totalFiles, List<string> extractedFiles, int extractedCount)
        {
            try
            {
                string status = extractedCount == totalFiles ? "Complete" : "In Progress";
                
                var metadata = new
                {
                    Version = version,
                    Key = keyBytes != null ? BitConverter.ToString(keyBytes).Replace("-", "") : null,
                    TotalFileCount = totalFiles,
                    ExtractedFileCount = extractedCount,
                    ExtractionStatus = status,
                    LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Files = extractedFiles.ToList() // Create a copy to avoid reference issues
                };
                
                string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                
                // Use atomic write operation to prevent corruption during cancellation
                string tempPath = metadataPath + ".tmp";
                File.WriteAllText(tempPath, json);
                
                // Replace the original file atomically
                if (File.Exists(metadataPath))
                    File.Delete(metadataPath);
                File.Move(tempPath, metadataPath);
                
                // Show progress every 10 files or at completion
                if (extractedCount % 10 == 0 || status == "Complete")
                {
                    Console.WriteLine($"Progress: {extractedCount}/{totalFiles} files - Metadata updated");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not update metadata: {ex.Message}");
            }
        }
    }
    
}
