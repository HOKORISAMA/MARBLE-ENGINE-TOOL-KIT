using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MarbleEngineTools
{
    public class PrsReader
    {
        public static readonly int[] LengthTable = new int[0x100];

        public PrsReader()
        {
            for (int i = 0; i < 0xFE; i++)
            {
                LengthTable[i] = i + 3;
            }
            LengthTable[0xFE] = 0x400;
            LengthTable[0xFF] = 0x1000;
        }

        public void ConvertPrsToPng(string inputPath, string outputPath)
        {
            if (File.Exists(inputPath))
            {
                // Single file conversion
                ConvertSingleFile(inputPath, outputPath);
            }
            else if (Directory.Exists(inputPath))
            {
                // Directory conversion
                ConvertDirectory(inputPath, outputPath);
            }
            else
            {
                throw new FileNotFoundException($"Input path not found: {inputPath}");
            }
        }

        private void ConvertSingleFile(string inputFile, string outputPath)
        {
            var metadata = ReadMetadata(inputFile);
            if (metadata == null)
            {
                Console.WriteLine($"Invalid PRS file: {inputFile}");
                return;
            }

            using var fs = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
            var imageData = UnpackPrs(fs, metadata);
            var image = CreateImage(imageData, metadata);

            string outputFile = Path.HasExtension(outputPath) ? outputPath : 
                Path.Combine(outputPath, Path.GetFileNameWithoutExtension(inputFile) + ".png");

            // Ensure output directory exists
            string outputDir = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            image.Save(outputFile);
            Console.WriteLine($"Converted: {Path.GetFileName(inputFile)} -> {Path.GetFileName(outputFile)}");
        }

        private void ConvertDirectory(string inputDir, string outputDir)
        {
            Directory.CreateDirectory(outputDir);

            var prsFiles = Directory.GetFiles(inputDir, "*.prs", SearchOption.TopDirectoryOnly);
            if (prsFiles.Length == 0)
            {
                Console.WriteLine($"No PRS files found in: {inputDir}");
                return;
            }

            Console.WriteLine($"Found {prsFiles.Length} PRS files to convert...");

            foreach (var inputFile in prsFiles)
            {
                try
                {
                    var metadata = ReadMetadata(inputFile);
                    if (metadata == null)
                    {
                        Console.WriteLine($"Skipping invalid PRS file: {Path.GetFileName(inputFile)}");
                        continue;
                    }

                    using var fs = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
                    var imageData = UnpackPrs(fs, metadata);
                    var image = CreateImage(imageData, metadata);

                    string outputFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(inputFile) + ".png");
                    image.Save(outputFile);
                    Console.WriteLine($"Converted: {Path.GetFileName(inputFile)} -> {Path.GetFileName(outputFile)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error converting {Path.GetFileName(inputFile)}: {ex.Message}");
                }
            }
        }

        private PrsMetaData ReadMetadata(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);

                byte[] header = br.ReadBytes(16);
                if (header.Length < 16)
                    return null;

                // Check magic bytes 'YB'
                if (header[0] != 'Y' || header[1] != 'B')
                    return null;

                int bpp = header[3];
                if (bpp != 3 && bpp != 4)
                    return null;

                return new PrsMetaData(
                    BitConverter.ToUInt16(header, 12), // Width
                    BitConverter.ToUInt16(header, 14), // Height  
                    8 * bpp,                          // Bits per pixel
                    header[2],                        // Flag
                    BitConverter.ToInt32(header, 4)   // Packed size
                );
            }
            catch
            {
                return null;
            }
        }

        private byte[] UnpackPrs(FileStream fs, PrsMetaData metadata)
        {
            int depth = metadata.Bpp / 8;
            int stride = metadata.Width * depth;
            byte[] output = new byte[stride * metadata.Height];

            fs.Seek(0x10, SeekOrigin.Begin);
            int dst = 0;
            int remaining = metadata.PackedSize;
            int bit = 0;
            int ctl = 0;

            while (remaining > 0 && dst < output.Length)
            {
                bit >>= 1;
                if (bit == 0)
                {
                    ctl = fs.ReadByte();
                    remaining--;
                    bit = 0x80;
                }

                if ((ctl & bit) == 0)
                {
                    output[dst++] = (byte)fs.ReadByte();
                    remaining--;
                    continue;
                }

                int b = fs.ReadByte();
                remaining--;

                int length = 0;
                int shift = 0;

                if ((b & 0x80) != 0)
                {
                    shift = fs.ReadByte();
                    remaining--;
                    shift |= (b & 0x3F) << 8;

                    if ((b & 0x40) != 0)
                    {
                        int offset = fs.ReadByte();
                        remaining--;
                        length = LengthTable[offset];
                    }
                    else
                    {
                        length = (shift & 0xF) + 3;
                        shift >>= 4;
                    }
                }
                else
                {
                    length = b >> 2;
                    b &= 3;
                    if (b == 3)
                    {
                        length += 9;
                        fs.Read(output, dst, length);
                        dst += length;
                        remaining -= length;
                        continue;
                    }
                    shift = length;
                    length = b + 2;
                }

                shift += 1;
                if (dst < shift)
                    throw new InvalidDataException("Invalid offset value");

                length = Math.Min(length, output.Length - dst);

                for (int i = 0; i < length; i++)
                {
                    output[dst] = (byte)((output[dst - shift] + output[dst]) % 256);
                    dst++;
                }
            }

            // Apply delta filter if flag is set
            if ((metadata.Flag & 0x80) != 0)
            {
                for (int i = depth; i < output.Length; i++)
                {
                    output[i] = (byte)((output[i] + output[i - depth]) % 256);
                }
            }

            // Convert colors (swap R and B channels)
            ConvertColors(output, depth);

            return output;
        }

        private void ConvertColors(byte[] output, int depth)
        {
            if (depth == 3) // RGB
            {
                for (int i = 0; i < output.Length; i += 3)
                {
                    (output[i], output[i + 2]) = (output[i + 2], output[i]); // Swap R and B
                }
            }
            else if (depth == 4) // RGBA
            {
                for (int i = 0; i < output.Length; i += 4)
                {
                    (output[i], output[i + 2]) = (output[i + 2], output[i]); // Swap R and B
                }
            }
        }

        private Image CreateImage(byte[] imageData, PrsMetaData metadata)
        {
            int depth = metadata.Bpp / 8;
            int stride = metadata.Width * depth;

            if (depth == 3) // RGB
            {
                var image = new Image<Rgb24>(metadata.Width, metadata.Height);
                for (int y = 0; y < metadata.Height; y++)
                {
                    for (int x = 0; x < metadata.Width; x++)
                    {
                        int idx = (y * stride) + (x * 3);
                        image[x, y] = new Rgb24(imageData[idx], imageData[idx + 1], imageData[idx + 2]);
                    }
                }
                return image;
            }
            else // RGBA
            {
                var image = new Image<Rgba32>(metadata.Width, metadata.Height);
                for (int y = 0; y < metadata.Height; y++)
                {
                    for (int x = 0; x < metadata.Width; x++)
                    {
                        int idx = (y * stride) + (x * 4);
                        image[x, y] = new Rgba32(imageData[idx], imageData[idx + 1], imageData[idx + 2], imageData[idx + 3]);
                    }
                }
                return image;
            }
        }
    }
}
