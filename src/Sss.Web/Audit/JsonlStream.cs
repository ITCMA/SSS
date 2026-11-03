using System.Security.Cryptography;
using System.Text;

namespace Sss.Web.Audit;

/// <summary>Append-only JSON-lines file per day for one stream ("audit" or "ops"). Every line carries a <c>chain</c> value,
/// the SHA-256 of the previous line's chain and this line's own text, so a changed, removed or inserted line in the middle of a
/// file shows up in <see cref="Verify"/>. The file is opened exclusively for each write, so two worker processes (IIS
/// overlapped recycling) cannot interleave and break the chain.</summary>
public sealed class JsonlStream(string directory, string stream, TimeProvider clock, bool perServerFiles)
{
    private const string ChainKey = ",\"chain\":\"";
    private readonly object _gate = new();

    public string Directory => directory;
    public string Stream => stream;

    public string PathFor(DateTimeOffset day) =>
        Path.Combine(directory, perServerFiles ? $"{stream}-{day:yyyyMMdd}-{Environment.MachineName}.jsonl" : $"{stream}-{day:yyyyMMdd}.jsonl");

    /// <summary>Appends one record; <paramref name="json"/> is a complete JSON object without the chain value.</summary>
    public void Append(string json)
    {
        var path = PathFor(clock.GetUtcNow());
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(directory);
            using var fs = OpenExclusive(path);
            var prev = LastChain(fs);
            var line = Seal(json, prev) + "\n";
            fs.Seek(0, SeekOrigin.End);
            var bytes = new UTF8Encoding(false).GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
        }
    }

    private static FileStream OpenExclusive(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 40) { Thread.Sleep(50); }
        }
    }

    public static string Seal(string json, string prevChain)
    {
        var chain = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(prevChain + "|" + json)));
        return json[..^1] + ChainKey + chain + "\"}";
    }

    private static string LastChain(FileStream fs)
    {
        if (fs.Length == 0) return "";
        var take = (int)Math.Min(fs.Length, 16384);
        var buf = new byte[take];
        fs.Seek(-take, SeekOrigin.End);
        fs.ReadExactly(buf, 0, take);
        var text = Encoding.UTF8.GetString(buf).TrimEnd('\n', '\r');
        var line = text[(text.LastIndexOf('\n') + 1)..];
        return ChainOf(line) ?? "";
    }

    private static string? ChainOf(string line)
    {
        var i = line.LastIndexOf(ChainKey, StringComparison.Ordinal);
        return i < 0 ? null : line[(i + ChainKey.Length)..].TrimEnd('}', '"');
    }

    /// <summary>Checks the chain of one file. Returns the number of lines and the first line (1-based) that does not fit, or 0.</summary>
    public static (int Lines, int FirstBad) Verify(string path)
    {
        var prev = "";
        var n = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            n++;
            var i = line.LastIndexOf(ChainKey, StringComparison.Ordinal);
            if (i < 0 || Seal(line[..i] + "}", prev) != line) return (n, n);
            prev = ChainOf(line)!;
        }
        return (n, 0);
    }
}
