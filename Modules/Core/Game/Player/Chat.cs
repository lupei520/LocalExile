using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // The global and trade channel of every character on this server (ChatChannels.cs).
    internal static readonly ChatChannels Channels = new();

    // C→S 0x134: the client joins a global or trade channel after entering an area and for /global N and /trade N, and
    // prints "Timeout while joining … chat channel" when no 0x135 comes within 5 s. The join is answered with 0x135, then
    // a chat line naming the channel when it is new for the character or was asked for by a command.
    internal static async Task JoinChatChannel(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceChat.JoinChannelRequest join)
    {
        var (channel, changed) = Channels.Join(CharacterName(conn), join.Kind, join.Channel, join.Language);
        string asked = join.Channel == InstanceChat.AnyChannel ? "any" : join.Channel.ToString();
        Console.WriteLine($"[gateway] {remote} <= 0x0134 join {join.Kind} chat channel {asked} ({(join.Automatic ? "on entering" : "command")}, language {join.Language})");
        await Send(remote, stream, ciphers, InstanceChat.ChannelJoined(join.Kind, channel, join.Language), $"0x0135 joined {join.Kind} chat channel {channel}");
        if (changed || !join.Automatic)
        {
            string text = ChatChannels.JoinedText(join.Kind, channel);
            await Send(remote, stream, ciphers, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
        }
    }

    // Every area: the channels the character joined earlier in the session (S→C 0x135), since the new instance connection
    // starts without any and the client asks only once per session.
    internal static async Task SendChatChannels(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        foreach (var (kind, channel, language) in Channels.Current(CharacterName(conn)))
            await Send(remote, stream, ciphers, InstanceChat.ChannelJoined(kind, channel, language), $"0x0135 still on {kind} chat channel {channel}");
    }

    // C→S 0x15 without a slash: the player's own line comes back as a chat line from the character, on the channel it was
    // typed in (local, '#' global, '$' trade), and everyone else on that channel gets the same line with the speaker's
    // name — local goes to the player's own instance, global and trade to every character that joined that number.
    internal static async Task EchoChat(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string typed)
    {
        // '@name text' is a whisper: it goes to that one player, and the speaker gets a "To ..." line back instead of
        // their own text (the client would otherwise show the whisper as something they said in public).
        var (typedChannel, body) = InstanceChat.SplitChannel(typed);
        if (typedChannel == TypedChannel.Whisper)
        {
            await Whisper(remote, stream, ciphers, conn, body);
            return;
        }
        if (ChatChannels.Echo(CharacterName(conn), typed) is not { } line) return;
        // Send encrypts in place: the speaker gets a copy, so `line` stays plaintext for the others (a ciphertext
        // clone decrypted with another connection's stream reaches the client as garbage).
        await Send(remote, stream, ciphers, (byte[])line.Clone(), $"0x0017 chat line from {CharacterName(conn)}");
        var (channel, _) = InstanceChat.SplitChannel(typed);
        if (channel is not (TypedChannel.Local or TypedChannel.Global or TypedChannel.Trade)) return;
        string speaker = CharacterName(conn);
        ushort number = channel switch
        {
            TypedChannel.Global => Channels.ChannelOf(speaker, ChatChannelKind.Global),
            TypedChannel.Trade => Channels.ChannelOf(speaker, ChatChannelKind.Trade),
            _ => 0,
        };
        int sent = 0;
        foreach (var other in InGame.Keys)
        {
            // Only a connection that has its player object and an area can take a 0x17: one still loading an area reads
            // the line as part of its own setup and the stream desyncs ("Unable to deserialise packet with pid ...").
            if (ReferenceEquals(other, conn)
                || other is not { Outbox: { } outbox, Closed: false, LeavingArea: false, PlayerObjectCreated: true }
                || other.Instance is null) continue;
            bool theirs = channel switch
            {
                TypedChannel.Local => ReferenceEquals(other.Instance, conn.Instance),
                TypedChannel.Global => Channels.IsOn(CharacterName(other), ChatChannelKind.Global, number),
                TypedChannel.Trade => Channels.IsOn(CharacterName(other), ChatChannelKind.Trade, number),
                _ => false,
            };
            if (!theirs) continue;
            // Fire and forget: the recipient writes through its own send gate, and the speaker's read loop must not wait
            // on another connection's socket.
            _ = outbox((byte[])line.Clone(), null);
            sent++;
        }
        if (sent > 0) Console.WriteLine($"[gateway] {remote} chat {channel} line to {sent} other player(s)");
    }

    /// <summary>
    /// '@name text': that character gets an S→C 0x17 whose sender starts with '@' (the client colours a line by its
    /// sender's first character, the same convention as '#' and '$'), and the speaker gets "To name: text" back so
    /// their own panel shows what they sent. A name that is not in game answers so instead.
    /// </summary>
    internal static async Task Whisper(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string body)
    {
        int space = body.IndexOf(' ');
        string name = space > 0 ? body[..space] : "";
        string text = space > 0 ? body[(space + 1)..].Trim() : "";
        string me = CharacterName(conn);
        if (name.Length == 0 || text.Length == 0)
        {
            await Send(remote, stream, ciphers, InstanceChat.ChatLine("用法：@名字 内容"), "0x0017 whisper usage");
            return;
        }
        var target = InGame.Keys.FirstOrDefault(o => !ReferenceEquals(o, conn)
            && o is { Outbox: { } outbox, Closed: false, LeavingArea: false, PlayerObjectCreated: true }
            && string.Equals(CharacterName(o), name, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.Outbox is not { } targetOutbox)
        {
            await Send(remote, stream, ciphers, InstanceChat.ChatLine($"{name} 不在线。"), $"0x0017 whisper to {name}: not online");
            return;
        }
        // The recipient writes through its own send gate: fire and forget, as the channel lines do.
        _ = targetOutbox(InstanceChat.ChatLine(text, "@" + me), null);
        await Send(remote, stream, ciphers, InstanceChat.ChatLine($"To {CharacterName(target)}: {text}"), $"0x0017 whisper to {name}");
        Console.WriteLine($"[gateway] {remote} whisper {me} -> {CharacterName(target)}: \"{text}\"");
    }
}
