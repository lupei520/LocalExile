using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The forge page's item pictures (Monitoring/MonitorForgePage.cs): the client's own inventory art for a base, handed to
/// the page as a PNG data URL. The art is decoded offline into <c>data/item-art</c> by <c>ggdump art</c>
/// (scripts/dev/extract-data.ps1's <c>item-art</c> entry), because the GGPK's art bundles are Selkie-compressed and the
/// in-process Oodle decoder reads Kraken and Mermaid only — and opening the client's 68 GB archive to show an 80x80 icon
/// would be the wrong trade for a running server.
/// </summary>
/// <remarks>
/// One read of <c>index.json</c> gives every base's file and size; the PNG bytes are then kept in memory, the most recent
/// few dozen only (an icon is a few kilobytes, and a page asks for the base the admin just picked). A missing index or
/// file is an answer with a reason, never a failed request: the page simply shows no picture.
/// </remarks>
static class ItemArt
{
    const string Folder = "data/item-art";
    const int KeepImages = 96;

    /// <param name="File">The PNG's name inside <c>data/item-art</c>.</param>
    /// <param name="Width">Its size, so the page can hold the space before the image arrives.</param>
    sealed record Picture(string File, int Width, int Height);

    static readonly Lazy<Dictionary<string, Picture>> Index = new(LoadIndex);

    static readonly object Gate = new();
    static readonly Dictionary<string, byte[]> Images = new(StringComparer.OrdinalIgnoreCase);
    static readonly Queue<string> Recent = new();

    /// <summary>The PNG file name inside the folder for a base id, or null. The exchange lists it as
    /// /art/<file>, which <see cref="Png"/> serves; the forge keeps the data URL of <see cref="Answer"/>.</summary>
    public static string? FileOf(string baseId) =>
        Index.Value.TryGetValue(baseId, out Picture? picture) ? picture.File : null;

    /// <summary>A picture by its file name, or null when the name is not a plain file of the folder: the
    /// /art route serves this, one request per icon instead of a data URL each.</summary>
    public static byte[]? Png(string? file)
    {
        if (file is not { Length: > 0 } || file.Contains('/') || file.Contains('\\') || file.Contains("..")
            || !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
        lock (Gate)
        {
            if (Images.TryGetValue(file, out byte[]? cached)) return cached;
            try
            {
                byte[] png = File.ReadAllBytes(Path.Combine(Folder, file));
                Images[file] = png;
                Recent.Enqueue(file);
                while (Recent.Count > KeepImages) Images.Remove(Recent.Dequeue());
                return png;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>One base's picture as JSON: a <c>data:</c> URL the page can put straight into an <c>&lt;img&gt;</c>.</summary>
    public static string Answer(string baseId)
    {
        if (string.IsNullOrWhiteSpace(baseId)) return Fail("base is required");
        if (!Index.Value.TryGetValue(baseId, out Picture? picture)) return Fail($"no picture for \"{baseId}\"");
        byte[]? png;
        lock (Gate)
        {
            if (!Images.TryGetValue(picture.File, out png))
            {
                string path = Path.Combine(Folder, picture.File);
                try
                {
                    png = File.ReadAllBytes(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return Fail($"could not read {path}: {e.Message}");
                }
                Images[picture.File] = png;
                Recent.Enqueue(picture.File);
                while (Recent.Count > KeepImages) Images.Remove(Recent.Dequeue());
            }
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["width"] = picture.Width,
            ["height"] = picture.Height,
            ["data"] = "data:image/png;base64," + Convert.ToBase64String(png),
        }.ToJsonString();
    }

    /// <summary>The base-to-file map <c>ggdump art</c> wrote, or an empty one with one line saying so.</summary>
    static Dictionary<string, Picture> LoadIndex()
    {
        var map = new Dictionary<string, Picture>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(Folder, "index.json");
        if (!File.Exists(path))
        {
            Console.WriteLine($"[monitor] no item art at {path}: scripts/dev/extract-data.ps1 -Only item-art writes it");
            return map;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("bases", out var bases)) return map;
            foreach (var entry in bases.EnumerateObject())
                if (entry.Value.TryGetProperty("file", out var file) && file.GetString() is { Length: > 0 } name)
                    map[entry.Name] = new Picture(name,
                        entry.Value.TryGetProperty("width", out var w) && w.TryGetInt32(out int width) ? width : 0,
                        entry.Value.TryGetProperty("height", out var h) && h.TryGetInt32(out int height) ? height : 0);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[monitor] {path} could not be read: {e.Message}");
        }
        return map;
    }

    static string Fail(string reason) => new JsonObject { ["ok"] = false, ["error"] = reason }.ToJsonString();
}
