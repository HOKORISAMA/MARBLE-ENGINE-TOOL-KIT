using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Marble
{
    public class PrsWriter
    {
        private readonly PrsMetaData _metadata;
        private readonly byte[] _input;
        private readonly int _depth;
        private readonly int _stride;
        private readonly MemoryStream _output;
        private readonly Dictionary<uint, List<int>> _hashTable;

        public PrsWriter(Image image, byte flag)
        {
            _metadata = new PrsMetaData(image.Width, image.Height, image.PixelType.BitsPerPixel, flag, 0);
            _depth = _metadata.Bpp / 8;
            _stride = _metadata.Width * _depth;
            _input = new byte[_stride * _metadata.Height];
            _output = new MemoryStream();
            _hashTable = new Dictionary<uint, List<int>>();

            if (image.PixelType.BitsPerPixel == 32 && image is Image<Rgba32> rgbaImage)
            {
                ConvertImageToByteArray(rgbaImage);
            }
            else if (image.PixelType.BitsPerPixel == 24 && image is Image<Rgb24> rgbImage)
            {
                ConvertImageToByteArray(rgbImage);
            }
            else
            {
                throw new NotSupportedException("Unsupported pixel format.");
            }

            if ((_metadata.Flag & 0x80) != 0)
            {
                for (int i = _input.Length - 1; i >= _depth; i--)
                {
                    _input[i] = (byte)((_input[i] - _input[i - _depth] + 256) % 256);
                }
            }
        }

        private void ConvertImageToByteArray(Image<Rgba32> image)
        {
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<Rgba32> pixelRow = accessor.GetRowSpan(y);
                    for (int x = 0; x < pixelRow.Length; x++)
                    {
                        int idx = (y * _stride) + (x * _depth);
                        _input[idx] = pixelRow[x].B;
                        _input[idx + 1] = pixelRow[x].G;
                        _input[idx + 2] = pixelRow[x].R;
                        if (_depth == 4)
                        {
                            _input[idx + 3] = pixelRow[x].A;
                        }
                    }
                }
            });
        }

        private void ConvertImageToByteArray(Image<Rgb24> image)
        {
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<Rgb24> pixelRow = accessor.GetRowSpan(y);
                    for (int x = 0; x < pixelRow.Length; x++)
                    {
                        int idx = (y * _stride) + (x * _depth);
                        _input[idx] = pixelRow[x].B;
                        _input[idx + 1] = pixelRow[x].G;
                        _input[idx + 2] = pixelRow[x].R;
                    }
                }
            });
        }

        private uint Hash(int index)
        {
            if (index + 3 > _input.Length) return 0;
            return (uint)(_input[index] | (_input[index + 1] << 8) | (_input[index + 2] << 16));
        }

        public void Pack()
        {
            _output.WriteByte((byte)'Y');
            _output.WriteByte((byte)'B');
            _output.WriteByte(_metadata.Flag);
            _output.WriteByte((byte)(_metadata.Bpp / 8));
            _output.Write(new byte[8], 0, 8); // Placeholder for packed size
            _output.Write(BitConverter.GetBytes((ushort)_metadata.Width), 0, 2);
            _output.Write(BitConverter.GetBytes((ushort)_metadata.Height), 0, 2);

            int inputIndex = 0;
            byte control = 0;
            byte mask = 1;
            List<byte> controlBuffer = new List<byte>();

            while (inputIndex < _input.Length)
            {
                int matchLength;
                int matchOffset;
                FindLongestMatch(inputIndex, out matchLength, out matchOffset);

                if (matchLength < 3)
                {
                    controlBuffer.Add(_input[inputIndex]);
                    inputIndex++;
                }
                else
                {
                    control |= mask;
                    if (matchLength <= 5 && matchOffset <= 256)
                    {
                        byte encodedValue = (byte)((matchOffset - 1) | ((matchLength - 2) << 6));
                        controlBuffer.Add(encodedValue);
                    }
                    else
                    {
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
                    _output.WriteByte(control);
                    _output.Write(controlBuffer.ToArray(), 0, controlBuffer.Count);
                    controlBuffer.Clear();
                    control = 0;
                    mask = 1;
                }

                for (int i = inputIndex - matchLength; i < inputIndex; i++)
                {
                    uint hash = Hash(i);
                    if (!_hashTable.ContainsKey(hash))
                        _hashTable[hash] = new List<int>();
                    _hashTable[hash].Add(i);
                }
            }

            if (mask != 1 || controlBuffer.Count > 0)
            {
                _output.WriteByte(control);
                _output.Write(controlBuffer.ToArray(), 0, controlBuffer.Count);
            }

            _metadata.PackedSize = (int)_output.Length - 16;
            _output.Seek(4, SeekOrigin.Begin);
            _output.Write(BitConverter.GetBytes(_metadata.PackedSize), 0, 4);
        }

        private void FindLongestMatch(int inputIndex, out int matchLength, out int matchOffset)
        {
            matchLength = 0;
            matchOffset = 0;

            uint hash = Hash(inputIndex);
            if (!_hashTable.ContainsKey(hash))
                return;

            int maxOffset = Math.Min(inputIndex, 0x2000);
            int maxLength = Math.Min(_input.Length - inputIndex, 0x100);

            foreach (int offset in _hashTable[hash])
            {
                if (inputIndex - offset > maxOffset)
                    continue;

                int length = 0;
                while (length < maxLength && _input[inputIndex + length] == _input[offset + length])
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
                _output.WriteTo(fileStream);
            }
        }
    }

    public class PngToPrsConverter
    {
        public static void ConvertPngToPrs(string inputDir, string outputDir)
        {
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            foreach (var fileName in Directory.GetFiles(inputDir, "*.png"))
            {
                using (Image image = Image.Load(fileName))
                {
                    byte flag = (byte)((image.PixelType.BitsPerPixel == 32) ? 0x80 : 0x00);
                    var writer = new PrsWriter(image, flag);
                    writer.Pack();

                    string outputFilePath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(fileName) + ".prs");
                    writer.SaveToFile(outputFilePath);
                    Console.WriteLine($"Converted {fileName} to {outputFilePath}");
                }
            }
        }
    }
}