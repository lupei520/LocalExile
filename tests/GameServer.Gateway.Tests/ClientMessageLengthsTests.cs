using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The C→S read loop has no length prefix, so <see cref="InstanceProtocol.SplitClientMessages"/> frames a read by
/// looking each id up. An id it does not know ends framing, and every message behind it in the same read is lost —
/// which is how the fixed-size writers the extraction found ended up dropped, the 0x1b tick among them.
/// </summary>
public class ClientMessageLengthsTests
{
    /// <summary>Ids the extraction covers, including one of the 16 whose 3.29.3b id is shifted to its 3.29.3c one.</summary>
    [Theory]
    [InlineData(0x000e)]
    [InlineData(0x2ce)]
    [InlineData(0x2f6)]
    public void AnExtractedFixedWriterDoesNotEndFraming(int id)
    {
        int? known = InstanceProtocol.KnownClientLength((ushort)id);
        Assert.NotNull(known);
        int length = known.Value;
        // The message, then the tick the latency acks depend on: before the table the tick was never reached.
        byte[] read = [.. Message((ushort)id, length), .. Message(0x1b, 7)];

        var messages = InstanceProtocol.SplitClientMessages(read, out byte[] partial, out byte[] unknown);

        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(new[] { id, 0x1b }, messages.Select(m => (int)m.Id));
        Assert.Equal(new[] { length, 7 }, messages.Select(m => m.Bytes.Length));
    }

    /// <summary>The table is generated from the extraction; this fails when one of the two moved without the other.</summary>
    [Fact]
    public void TheTableIsTheExtractedFixedWriters()
    {
        var extracted = ExtractedInstanceLengths();
        Assert.Equal(ClientMessageLengths.Count, extracted.Count);
        foreach ((int id, int length) in extracted)
            Assert.Equal(length, InstanceProtocol.KnownClientLength((ushort)id));
    }

    /// <summary>
    /// c2s.md is a 3.29.3b extraction and the server speaks 3.29.3c, so its instance ids move as the 3.29.3c diff
    /// records: one higher from 0x2a8, two higher from 0x2b0. scripts/re/c2s_lengths.py applies the same shift.
    /// </summary>
    static Dictionary<int, int> ExtractedInstanceLengths()
    {
        var lengths = new Dictionary<int, int>();
        bool inInstance = false;
        foreach (string line in File.ReadLines(Path.Combine("Agents", "docs", "protocol", "c2s.md")))
        {
            if (line.StartsWith("## "))
                inInstance = line.StartsWith("## Instance connection");
            else if (inInstance && line.StartsWith("| "))
            {
                string[] columns = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (columns.Length >= 3 && columns[0].StartsWith("0x") && int.TryParse(columns[2], out int length))
                {
                    int id = Convert.ToInt32(columns[0], 16);
                    lengths[id < 0x2a8 ? id : id < 0x2b0 ? id + 1 : id + 2] = length;
                }
            }
        }
        Assert.NotEmpty(lengths);
        return lengths;
    }

    static byte[] Message(ushort id, int length)
    {
        byte[] message = new byte[length];
        message[0] = (byte)(id >> 8);
        message[1] = (byte)id;
        return message;
    }
}
