using System.Buffers.Binary;
using System.IO.Compression;

namespace WindowsAiOperator.Core.Automation;

/// <summary>Minimal PNG encoder (8-bit RGB) for generated test images and the mock desktop. No dependencies.</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="rgb">width*height*3 bytes, row-major.</param>
    public static byte[] Encode(int width, int height, byte[] rgb)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (rgb.Length != width * height * 3)
        {
            throw new ArgumentException("픽셀 버퍼 크기가 맞지 않습니다.", nameof(rgb));
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // color type RGB
        WriteChunk(output, "IHDR", header);

        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var stride = width * 3;
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // filter: none
                zlib.Write(rgb, y * stride, stride);
            }
        }

        WriteChunk(output, "IDAT", raw.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>A solid image with a filled rectangle — used by the vision test and the mock desktop.</summary>
    public static byte[] SolidWithRectangle(int width, int height, (byte R, byte G, byte B) background, PixelRect rect, (byte R, byte G, byte B) color)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var inside = x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
                var (r, g, b) = inside ? color : background;
                var i = (y * width + x) * 3;
                rgb[i] = r;
                rgb[i + 1] = g;
                rgb[i + 2] = b;
            }
        }

        return Encode(width, height, rgb);
    }

    /// <summary>8x8 average-luminance signature of an RGB buffer (64 bits), for "did the screen change" checks.</summary>
    public static ulong Signature(int width, int height, byte[] rgb)
    {
        Span<double> cells = stackalloc double[64];
        Span<int> counts = stackalloc int[64];
        for (var y = 0; y < height; y++)
        {
            var cy = Math.Min(7, y * 8 / height);
            for (var x = 0; x < width; x++)
            {
                var cx = Math.Min(7, x * 8 / width);
                var i = (y * width + x) * 3;
                cells[cy * 8 + cx] += 0.299 * rgb[i] + 0.587 * rgb[i + 1] + 0.114 * rgb[i + 2];
                counts[cy * 8 + cx]++;
            }
        }

        double mean = 0;
        for (var i = 0; i < 64; i++)
        {
            cells[i] = counts[i] == 0 ? 0 : cells[i] / counts[i];
            mean += cells[i];
        }

        mean /= 64;
        ulong signature = 0;
        for (var i = 0; i < 64; i++)
        {
            if (cells[i] > mean)
            {
                signature |= 1UL << i;
            }
        }

        return signature;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = 0xFFFFFFFFu;
        crc = Update(crc, typeBytes);
        crc = Update(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        stream.Write(crcBytes);
    }

    private static uint Update(uint crc, byte[] data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
