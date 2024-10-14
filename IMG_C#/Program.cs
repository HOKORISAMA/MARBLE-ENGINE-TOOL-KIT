using System;
using System.IO;
using System.Collections.Generic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

public class PrsMetaData
{
    public int Width { get; }
    public int Height { get; }
    public int Bpp { get; }
    public byte Flag { get; }
    public int PackedSize { get; set; }

    public PrsMetaData(int width, int height, int bpp, byte flag)
    {
        Width = width;
        Height = height;
        Bpp = bpp;
        Flag = flag;
        PackedSize = 0; // Will be set after compression
    }
}

public class PrsWriter
{
    private readonly PrsMetaData metadata;
    private readonly byte[] input;
    private readonly int depth;
    private readonly int stride;
    private readonly MemoryStream output;
    private readonly Dictionary<uint, List<int>> hashTable;

    public PrsWriter(Image<Rgba32> image, byte flag)
    {
        metadata = new PrsMetaData(image.Width, image.Height, image.PixelType.BitsPerPixel, flag);
        depth = metadata.Bpp / 8;
        stride = metadata.Width * depth;
        input = new byte[stride * metadata.Height];
        output = new MemoryStream();
        hashTable = new Dictionary<uint, List<int>>();

        // Convert image to byte array
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < pixelRow.Length; x++)
                {
                    int idx = (y * stride) + (x * depth);
                    input[idx] = pixelRow[x].B;
                    input[idx + 1] = pixelRow[x].G;
                    input[idx + 2] = pixelRow[x].R;
                    if (depth == 4)
                    {
                        input[idx + 3] = pixelRow[x].A;
                    }
                }
            }
        });

        if ((metadata.Flag & 0x80) != 0)
        {
            for (int i = input.Length - 1; i >= depth; i--)
            {
                input[i] = (byte)((input[i] - input[i - depth] + 256) % 256);
            }
        }
    }

    private uint Hash(int index)
    {
        if (index + 3 > input.Length) return 0;
        return (uint)(input[index] | (input[index + 1] << 8) | (input[index + 2] << 16));
    }

    public void Pack()
    {
        // Write header
        output.WriteByte((byte)'Y');
        output.WriteByte((byte)'B');
        output.WriteByte(metadata.Flag);
        output.WriteByte((byte)(metadata.Bpp / 8));
        output.Write(new byte[8], 0, 8); // Placeholder for packed size
        output.Write(BitConverter.GetBytes((ushort)metadata.Width), 0, 2);
        output.Write(BitConverter.GetBytes((ushort)metadata.Height), 0, 2);

        int inputIndex = 0;
        byte control = 0;
        byte mask = 1;
        List<byte> controlBuffer = new List<byte>();

        while (inputIndex < input.Length)
        {
            int matchLength;
            int matchOffset;
            FindLongestMatch(inputIndex, out matchLength, out matchOffset);

            if (matchLength < 3)
            {
                // Literal byte
                controlBuffer.Add(input[inputIndex]);
                inputIndex++;
            }
            else
            {
                // Set control bit
                control |= mask;

                if (matchLength <= 5 && matchOffset <= 256)
                {
                    // Short match
                    byte encodedValue = (byte)((matchOffset - 1) | ((matchLength - 2) << 6));
                    controlBuffer.Add(encodedValue);
                }
                else
                {
                    // Long match
                    ushort encodedOffset = (ushort)(matchOffset - 1);
                    if (matchLength <= 9)
                    {
                        byte encodedLength = (byte)(matchLength - 2);
                        controlBuffer.Add((byte)(0x80 | (encodedOffset >> 5)));
                        controlBuffer.Add((byte)((encodedOffset & 0x1F) | (encodedLength << 5)));
                    }
                    else
                    {
                        controlBuffer.Add((byte)(0xC0 | (encodedOffset >> 8)));
                        controlBuffer.Add((byte)(encodedOffset & 0xFF));
                        controlBuffer.Add((byte)(matchLength - 1));
                    }
                }

                inputIndex += matchLength;
            }

            mask <<= 1;
            if (mask == 0)
            {
                output.WriteByte(control);
                output.Write(controlBuffer.ToArray(), 0, controlBuffer.Count);
                controlBuffer.Clear();
                control = 0;
                mask = 1;
            }

            // Update hash table
            for (int i = inputIndex - matchLength; i < inputIndex; i++)
            {
                uint hash = Hash(i);
                if (!hashTable.ContainsKey(hash))
                    hashTable[hash] = new List<int>();
                hashTable[hash].Add(i);
            }
        }

        // Write final control byte and buffer if necessary
        if (mask != 1 || controlBuffer.Count > 0)
        {
            output.WriteByte(control);
            output.Write(controlBuffer.ToArray(), 0, controlBuffer.Count);
        }

        // Update packed size in header
        metadata.PackedSize = (int)output.Length - 16;
        output.Seek(4, SeekOrigin.Begin);
        output.Write(BitConverter.GetBytes(metadata.PackedSize), 0, 4);
    }

    private void FindLongestMatch(int inputIndex, out int matchLength, out int matchOffset)
    {
        matchLength = 0;
        matchOffset = 0;

        uint hash = Hash(inputIndex);
        if (!hashTable.ContainsKey(hash))
            return;

        int maxOffset = Math.Min(inputIndex, 0x2000);
        int maxLength = Math.Min(input.Length - inputIndex, 0x100);

        foreach (int offset in hashTable[hash])
        {
            if (inputIndex - offset > maxOffset)
                continue;

            int length = 0;
            while (length < maxLength && input[inputIndex + length] == input[offset + length])
            {
                length++;
            }

            if (length > matchLength)
            {
                matchLength = length;
                matchOffset = inputIndex - offset;
                if (matchLength == maxLength)
                    break;
            }
        }

        if (matchLength < 3)
        {
            matchLength = 0;
            matchOffset = 0;
        }
    }

    public void SaveToFile(string filePath)
    {
        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
        {
            output.WriteTo(fileStream);
        }
    }
}

public class Program
{
    public static void ConvertBmpToPrs(string inputDir, string outputDir)
    {
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        foreach (var fileName in Directory.GetFiles(inputDir, "*.bmp"))
        {
            using (Image<Rgba32> image = Image.Load<Rgba32>(fileName))
            {
                byte flag = (byte)(image.PixelType.BitsPerPixel == 32 ? 0x80 : 0x00);
                var writer = new PrsWriter(image, flag);
                writer.Pack();

                string outputFilePath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(fileName) + ".prs");
                writer.SaveToFile(outputFilePath);
                Console.WriteLine($"Converted {fileName} to {outputFilePath}");
            }
        }
    }

    public static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Bmp2Prs <input_dir> <output_dir>");
            return;
        }

        string inputDir = args[0];
        string outputDir = args[1];

        ConvertBmpToPrs(inputDir, outputDir);
    }
}