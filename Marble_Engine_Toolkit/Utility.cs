using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

public class Entry
{
    public string Name { get; set; }
    public int Offset { get; set; }
    public int Size { get; set; }
}

public class PrsMetaData
{
    public int Width { get; }
    public int Height { get; }
    public int Bpp { get; }
    public byte Flag { get; }
    public int PackedSize { get; set; }

    public PrsMetaData(int width, int height, int bpp, byte flag, int packedSize)
    {
        Width = width;
        Height = height;
        Bpp = bpp;
        Flag = flag;
        PackedSize = packedSize;
    }
}

public class ArcEncoding
{
    public static Encoding Shift_JIS = Encoding.GetEncoding(932);
}

public class Utility
{
    public static byte[] ConvertHexStringToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
            throw new ArgumentException("Invalid hex string");

        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
}

public class Xor
{
    public static byte[] XorDecrypt(byte[] data, byte[] key)
    {
        byte[] result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ key[i % key.Length]);
        }
        return result;
    }

    public static byte[] XorEncrypt(byte[] data, byte[] key)
    {
        return XorDecrypt(data, key);
    }
}

public class ArcOpen : IDisposable
{
    private FileStream _fileStream;
    private string _filename;
    public long FileSize { get; private set; }

    public ArcOpen(string file, FileMode mode = FileMode.Open)
    {
        _filename = file;
        _fileStream = new FileStream(file, mode, FileAccess.ReadWrite);
        FileSize = _fileStream.Length;
    }

    public long Position
    {
        get => _fileStream.Position;
        set => _fileStream.Position = value;
    }
    
    public void SetLength(long length)
    {
        _fileStream.SetLength(length);
        FileSize = _fileStream.Length;
    }

    public void WriteFixedLengthString(string str, int length, Encoding encoding = null)
    {
        encoding ??= ArcEncoding.Shift_JIS; // Use Shift_JIS by default if no encoding is provided
        byte[] strBytes = encoding.GetBytes(str);

        // Write string bytes
        _fileStream.Write(strBytes, 0, strBytes.Length);

        // Fill remaining space with zeros
        int remaining = length - strBytes.Length;
        if (remaining > 0)
        {
            _fileStream.Write(new byte[remaining], 0, remaining);
        }
    }


    // Seek to a position
    public void Seek(long offset, SeekOrigin origin = SeekOrigin.Begin)
    {
        if (offset < 0 || offset >= FileSize)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset is outside the bounds of the file.");
        }
        _fileStream.Seek(offset, origin);
    }

    public void Read(uint offset, byte[] buffer, int index, int count)
    {
        _fileStream.Seek(offset, SeekOrigin.Begin);
        _fileStream.ReadExactly(buffer, index, count);
    }
    
    // Read data from current position
    public byte[] Read(int count)
    {
        byte[] buffer = new byte[count];
        int bytesRead = _fileStream.Read(buffer, 0, count);
        if (bytesRead < count)
        {
            throw new EndOfStreamException("Reached the end of the stream before reading the requested amount of data.");
        }
        return buffer;
    }

    // Write data to current position
    public void Write(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            throw new ArgumentException("Data cannot be null or empty.", nameof(data));
        }
        _fileStream.Write(data, 0, data.Length);
        _fileStream.Flush();
        FileSize = _fileStream.Length;
    }

    // Seek and Read in one step
    public byte[] SeekAndRead(long offset, int count)
    {
        Seek(offset);
        return Read(count);
    }

    // Seek and Write in one step
    public void SeekAndWrite(long offset, byte[] data)
    {
        Seek(offset);
        Write(data);
    }

    public bool IsSaneCount(int count)
    {
        return count > 0 && count < 0x10000;
    }

    public void ExtractFile(Entry entry, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        _fileStream.Seek(entry.Offset, SeekOrigin.Begin);
        byte[] fileData = new byte[entry.Size];
        _fileStream.ReadExactly(fileData, 0, entry.Size);

        string outputPath = Path.Combine(outputDir, entry.Name);
        File.WriteAllBytes(outputPath, fileData);
    }

    public string Filename => _filename;

    public string ReadString(long startOffset, int nameLength)
    {
        _fileStream.Seek(startOffset, SeekOrigin.Begin);
        byte[] data = new byte[nameLength];
        _fileStream.ReadExactly(data, 0, nameLength);

        string result = Encoding.GetEncoding("shift_jis").GetString(data);
        int nullIndex = result.IndexOf('\0');
        return nullIndex >= 0 ? result.Substring(0, nullIndex) : result;
    }

    private T ReadValue<T>(long offset) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        if (offset < 0 || offset + size > FileSize)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"Offset {offset} is out of file bounds");
        }

        _fileStream.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[size];
        _fileStream.ReadExactly(buffer, 0, size);
        return MemoryMarshal.Read<T>(buffer);
    }

    private void WriteValue<T>(long offset, T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"Offset {offset} is out of file bounds");
        }

        if (offset + size > FileSize)
        {
            _fileStream.SetLength(offset + size);
            FileSize = _fileStream.Length;
        }

        _fileStream.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[size];
        MemoryMarshal.Write(buffer, in value);
        _fileStream.Write(buffer, 0, size);
        _fileStream.Flush();
    }

    private void WriteValue<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] buffer = new byte[size];
        MemoryMarshal.Write(buffer, in value);
        _fileStream.Write(buffer, 0, size);
        _fileStream.Flush();
    }

    public byte ReadUInt8(long offset) => ReadValue<byte>(offset);
    public sbyte ReadInt8(long offset) => ReadValue<sbyte>(offset);
    public ushort ReadUInt16(long offset) => ReadValue<ushort>(offset);
    public short ReadInt16(long offset) => ReadValue<short>(offset);
    public uint ReadUInt32(long offset) => ReadValue<uint>(offset);
    public int ReadInt32(long offset) => ReadValue<int>(offset);

    public void WriteUInt8(long offset, byte value) => WriteValue(offset, value);
    public void WriteUInt8(byte value) => WriteValue(value);
    public void WriteInt8(long offset, sbyte value) => WriteValue(offset, value);
    public void WriteInt8(sbyte value) => WriteValue(value);
    public void WriteUInt16(long offset, ushort value) => WriteValue(offset, value);
    public void WriteUInt16(ushort value) => WriteValue(value);
    public void WriteInt16(long offset, short value) => WriteValue(offset, value);
    public void WriteInt16(short value) => WriteValue(value);
    public void WriteUInt32(long offset, uint value) => WriteValue(offset, value);
    public void WriteUInt32(uint value) => WriteValue(value);
    public void WriteInt32(long offset, int value) => WriteValue(offset, value);
    public void WriteInt32(int value) => WriteValue(value);

    public void Backup(string backupPath = null)
    {
        backupPath ??= _filename + ".bak";

        _fileStream.Close();

        try
        {
            File.Copy(_filename, backupPath, overwrite: true);
            _fileStream = new FileStream(_filename, FileMode.Open, FileAccess.ReadWrite);
            FileSize = _fileStream.Length;
        }
        catch (Exception e)
        {
            throw new IOException($"Failed to create backup: {e.Message}", e);
        }
    }

    public void Dispose()
    {
        _fileStream?.Dispose();
    }

    public void Close()
    {
        Dispose();
    }
}
