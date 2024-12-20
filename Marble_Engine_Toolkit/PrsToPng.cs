using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

public class PrsReader
{
    private static readonly int[] LengthTable = new int[0x100];
    private readonly string _filePath;
    private readonly PrsMetaData _metadata;
    private readonly int _depth;
    private readonly byte[] _output;
    private readonly int _stride;
    private string _format;

    static PrsReader()
    {
        for (int i = 0; i < 0xFE; i++)
        {
            LengthTable[i] = i + 3;
        }
        LengthTable[0xFE] = 0x400;
        LengthTable[0xFF] = 0x1000;
    }

    public PrsReader(string filePath, PrsMetaData metadata)
    {
        this._filePath = filePath;
        this._metadata = metadata;
        _depth = metadata.Bpp / 8;
        _format = _depth == 4 ? "RGBA" : "RGB";
        _stride = metadata.Width * _depth;
        _output = new byte[_stride * metadata.Height];
    }

    public void Unpack()
    {
        using (var fileStream = new FileStream(_filePath, FileMode.Open, FileAccess.Read))
        {
            fileStream.Seek(0x10, SeekOrigin.Begin);
            int dst = 0;
            int remaining = _metadata.PackedSize;
            int bit = 0;
            int ctl = 0;

            while (remaining > 0 && dst < _output.Length)
            {
                bit >>= 1;
                if (bit == 0)
                {
                    ctl = fileStream.ReadByte();
                    remaining--;
                    bit = 0x80;
                }

                if ((ctl & bit) == 0)
                {
                    _output[dst++] = (byte)fileStream.ReadByte();
                    remaining--;
                    continue;
                }

                int b = fileStream.ReadByte();
                remaining--;

                int length = 0;
                int shift = 0;

                if ((b & 0x80) != 0)
                {
                    shift = fileStream.ReadByte();
                    remaining--;
                    shift |= (b & 0x3F) << 8;

                    if ((b & 0x40) != 0)
                    {
                        int offset = fileStream.ReadByte();
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
                        fileStream.ReadExactly(_output, dst, length);
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

                length = Math.Min(length, _output.Length - dst);

                for (int i = 0; i < length; i++)
                {
                    _output[dst] = (byte)((_output[dst - shift] + _output[dst]) % 256);
                    dst++;
                }
            }

            if ((_metadata.Flag & 0x80) != 0)
            {
                for (int i = _depth; i < _output.Length; i++)
                {
                    _output[i] = (byte)((_output[i] + _output[i - _depth]) % 256);
                }
            }

            if (_depth == 4 && IsDummyAlphaChannel())
            {
                _format = "RGB";
            }

            ConvertColors();
        }
    }

    private bool IsDummyAlphaChannel()
    {
        byte alpha = _output[3];
        if (alpha == 0xFF)
            return false;

        for (int i = 7; i < _output.Length; i += 4)
        {
            if (_output[i] != alpha)
                return false;
        }
        return true;
    }

    private void ConvertColors()
    {
        if (_format == "RGB")
        {
            // No conversion needed for 24bpp images
            for (int i = 0; i < _output.Length; i += 3)
            {
                (_output[i], _output[i + 2]) = (_output[i + 2], _output[i]);  // Swap Red and Blue
            }
        }
        else if (_format == "RGBA")
        {
            // Swap Red and Blue for 32bpp images with alpha channel
            for (int i = 0; i < _output.Length; i += 4)
            {
                (_output[i], _output[i + 2]) = (_output[i + 2], _output[i]);  // Swap Red and Blue
            }
        }
    }

    public Image GetImage()
    {
        if (_format == "RGB")
        {
            // For 24bpp images, create an Image<Rgb24> since it does not include an alpha channel
            var image = new Image<Rgb24>(_metadata.Width, _metadata.Height);
            for (int y = 0; y < _metadata.Height; y++)
            {
                for (int x = 0; x < _metadata.Width; x++)
                {
                    int idx = (y * _stride) + (x * 3);
                    image[x, y] = new Rgb24(_output[idx], _output[idx + 1], _output[idx + 2]);
                }
            }
            return image; // Return as Image<Rgb24> (24bpp)
        }
        else
        {
            // For 32bpp images, load as Rgba32
            var image = new Image<Rgba32>(_metadata.Width, _metadata.Height);
            for (int y = 0; y < _metadata.Height; y++)
            {
                for (int x = 0; x < _metadata.Width; x++)
                {
                    int idx = (y * _stride) + (x * 4); // 4 bytes per pixel for RGBA
                    image[x, y] = new Rgba32(_output[idx], _output[idx + 1], _output[idx + 2], _output[idx + 3]);
                }
            }
            return image; // Return as Image<Rgba32> (32bpp)
        }
    }

}

public static class PrsToPngConverter
{
    public static PrsMetaData ReadMetadata(string filePath)
    {
        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        {
            byte[] header = new byte[16];
            fileStream.ReadExactly(header, 0, 16);

            if (header[0] != 'Y' || header[1] != 'B')
                return null;

            int bpp = header[3];
            if (bpp != 3 && bpp != 4)
                return null;

            return new PrsMetaData(
                BitConverter.ToUInt16(header, 12),
                BitConverter.ToUInt16(header, 14),
                8 * bpp,
                header[2],
                BitConverter.ToInt32(header, 4)
            );
        }
    }

    public static void ConvertPrsToPng(string inputDir, string outputDir)
    {
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        foreach (var fileName in Directory.GetFiles(inputDir, "*.prs"))
        {
            var metadata = ReadMetadata(fileName);
            if (metadata != null)
            {
                var reader = new PrsReader(fileName, metadata);
                reader.Unpack();
                var image = reader.GetImage();
                string outputFilePath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(fileName) + ".png");
                image.Save(outputFilePath);
                Console.WriteLine($"Converted {fileName} to {outputFilePath}");
            }
        }
    }
}