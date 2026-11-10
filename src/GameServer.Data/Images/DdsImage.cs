using System.Buffers.Binary;

namespace GameServer.Data.Images;

/// <summary>
/// The client's item art (<c>Art/2DItems/**.dds</c>): a DDS whose top mip is plain RGBA8. Every inventory icon measured
/// (rings, amulets, belts, flasks, jewels, weapons) is stored uncompressed — DXGI_FORMAT_R8G8B8A8_UNORM (28), one mip
/// chain and nothing else — so reading one is a header and a copy, with no block decoder. That is what lets the monitor
/// page show an item's own picture without a drawing library or a texture pipeline.
/// </summary>
/// <remarks>
/// A <c>.dds</c> entry in the GGPK is a whole DDS file: the four magic bytes, a 124-byte header and, for these, a 20-byte
/// DX10 header, with the mip chain exactly filling the rest of the entry (an 80x80 ring is 34,268 bytes = 148 + 34,120).
/// A format this reader does not know is refused with its DXGI number rather than misread, and the client's companion
/// <c>.dds.header</c> file is not needed: it repeats this header for the streaming case.
/// </remarks>
public sealed record DdsImage(int Width, int Height, byte[] Rgba)
{
    const int HeaderSize = 4 + 124;
    const int Dx10Size = 20;
    const uint Magic = 0x20534444;    // "DDS "
    const uint Dx10FourCc = 0x30315844; // "DX10"
    const uint R8G8B8A8Unorm = 28;

    /// <summary>The top mip as RGBA8 rows (row-major, no padding), or null with the reason it could not be read.</summary>
    public static DdsImage? Read(ReadOnlySpan<byte> data, out string? error)
    {
        error = null;
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            error = "not a DDS file";
            return null;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 124)
        {
            error = "a DDS header that is not 124 bytes";
            return null;
        }
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        int mips = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(data[28..]));
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data[84..]);
        int at = HeaderSize;
        uint format = fourCc;
        if (fourCc == Dx10FourCc)
        {
            if (data.Length < HeaderSize + Dx10Size) { error = "a DX10 header that is cut short"; return null; }
            format = BinaryPrimitives.ReadUInt32LittleEndian(data[HeaderSize..]);
            at += Dx10Size;
        }
        if (format != R8G8B8A8Unorm)
        {
            error = fourCc == Dx10FourCc ? $"DXGI format {format}, not R8G8B8A8" : $"fourCC 0x{fourCc:x8}, not DX10";
            return null;
        }
        if (width <= 0 || height <= 0) { error = $"a size of {width}x{height}"; return null; }
        long top = (long)width * height * 4;
        if (at + top > data.Length) { error = "shorter than its top mip"; return null; }
        // The whole chain should fill the entry: a file that does not is some other layout, so refuse it rather than show
        // a picture of the wrong part of it. The client pads the smallest mips by a few bytes at most.
        long chain = 0;
        for (int i = 0; i < mips; i++) chain += (long)Math.Max(1, width >> i) * Math.Max(1, height >> i) * 4;
        if (chain > data.Length - at + 64) { error = $"a {mips}-mip chain that does not fit the file"; return null; }
        return new DdsImage(width, height, data.Slice(at, (int)top).ToArray());
    }
}
