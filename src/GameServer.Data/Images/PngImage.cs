using System.Buffers.Binary;
using System.IO.Compression;

namespace GameServer.Data.Images;

/// <summary>
/// A PNG of RGBA8 rows, for the monitor's pages: a browser gets the client's item art as an image, and neither the
/// Gateway nor the page needs a drawing library or a texture format of its own.
/// </summary>
/// <remarks>
/// A PNG is four chunks — IHDR, IDAT, IEND — each a length, a type, its bytes and a CRC32, and the image data is a zlib
/// stream of one filter byte plus one row per line. .NET has the zlib (<see cref="ZLibStream"/>) and no PNG writer, which
/// is the whole of what this is.
/// </remarks>
public static class PngImage
{
    /// <param name="rgba">Row-major RGBA8, at least <paramref name="width"/> * <paramref name="height"/> * 4 bytes.</param>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < (long)width * height * 4)
            throw new ArgumentException($"{rgba.Length} bytes is not a {width}x{height} RGBA8 image", nameof(rgba));

        int stride = width * 4;
        byte[] filtered = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            filtered[y * (stride + 1)] = 0; // filter none: the art is small and the deflate below does the work
            rgba.Slice(y * stride, stride).CopyTo(filtered.AsSpan(y * (stride + 1) + 1));
        }
        byte[] compressed;
        using (var deflated = new MemoryStream())
        {
            using (var zlib = new ZLibStream(deflated, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(filtered);
            compressed = deflated.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: truecolour with alpha
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>One PNG chunk: its length, its type, its bytes and a CRC32 over type and bytes.</summary>
    static void Chunk(Stream into, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        into.Write(length);
        Span<byte> name = stackalloc byte[4];
        for (int i = 0; i < 4; i++) name[i] = (byte)type[i];
        into.Write(name);
        into.Write(data);
        uint crc = Crc32(name, data);
        Span<byte> sum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sum, crc);
        into.Write(sum);
    }

    static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint crc = 0xffffffffu;
        foreach (byte value in a) crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        foreach (byte value in b) crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc ^ 0xffffffffu;
    }

    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
