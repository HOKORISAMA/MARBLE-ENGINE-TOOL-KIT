using System;
using System.IO;
using System.Text;

public class Entry
{
    public string Name { get; set; }
    public int Offset { get; set; }
    public int Size { get; set; }
	public String OriginalPath { get; set; }
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
	
	public static string ReadString(BinaryReader br, int length)
	{
		byte[] bytes = br.ReadBytes(length);
		int nullIndex = Array.IndexOf(bytes, (byte)0);
		if (nullIndex >= 0)
		{
			Array.Resize(ref bytes, nullIndex);
		}
		return ArcEncoding.Shift_JIS.GetString(bytes);
	}
	
	public static void WriteFixedLengthString(BinaryWriter bw, string str, int length)
	{
		byte[] bytes = new byte[length];
		if (!string.IsNullOrEmpty(str))
		{
			byte[] strBytes = ArcEncoding.Shift_JIS.GetBytes(str);
			Array.Copy(strBytes, bytes, Math.Min(strBytes.Length, length - 1));
		}
		bw.Write(bytes);
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
