using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DashyNMS.Mcp;

/// <summary>An answer kept for when LibreNMS can't give one.</summary>
/// <param name="Json">The result as it was returned: redacted, and within its cap.</param>
public sealed record KnownAnswer(string Json, DateTimeOffset AsOf);

/// <summary>
/// Each question's last good answer (#39), in memory and, when the operator
/// gives it a folder, on disk.
/// </summary>
/// <remarks>
/// <para>
/// Only what a tool returned is kept, after redaction, so nothing that was
/// redacted is in here; an answer anything was redacted from isn't kept at all.
/// </para>
/// <para>
/// Files are encrypted with a key made from the LibreNMS token and the settings
/// that shape an answer, and named by a keyed hash of the question. So another
/// account's answers, or another configuration's, can't be read or even matched,
/// and a file changed by someone else with access to the folder is ignored.
/// </para>
/// </remarks>
public sealed class LastKnownGood
{
    /// <summary>Answers kept at most. The one used longest ago goes first.</summary>
    public const int MaxAnswers = 500;

    /// <summary>The most all answers take, in characters.</summary>
    public const int MaxCharacters = 8 * 1024 * 1024;

    private const string FolderPrefix = "dashynms-";
    private const string FileExtension = ".answer";
    private const string FilePattern = "*" + FileExtension;
    private const int PruneEvery = 50;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, KnownAnswer Answer)>> _answers = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, KnownAnswer Answer)> _byUse = new();
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;
    private readonly ILogger<LastKnownGood> _logger;
    private readonly byte[] _fileKey;
    private readonly byte[] _nameKey;
    private string? _folder;
    private int _characters;
    private int _generation;
    private int _written;
    private bool _diskFailed;

    /// <param name="identity">The LibreNMS account and everything else an answer depends on. Never stored.</param>
    /// <param name="directory">Where to keep answers on disk too; null for memory alone.</param>
    public LastKnownGood(string identity, TimeSpan lifetime, string? directory, TimeProvider time, ILogger<LastKnownGood> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        _lifetime = lifetime;
        _time = time;
        _logger = logger;
        var secret = Encoding.UTF8.GetBytes(identity);
        _fileKey = Derive(secret, "file");
        _nameKey = Derive(secret, "name");

        // A folder of its own, so one account's answers can be dropped without touching another's.
        _folder = directory is null ? null : Path.Combine(directory, FolderPrefix + Convert.ToHexStringLower(Derive(secret, "folder").AsSpan(0, 16)));
        OnDisk(Prepare);
    }

    /// <summary>
    /// Changes whenever an answer is dropped. Take it before asking, and an
    /// answer from before the drop can't be kept after it.
    /// </summary>
    public int Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    /// <summary>Whether there is any answer on disk from before, so it's worth starting while LibreNMS can't be reached.</summary>
    public bool HasStoredAnswers
    {
        get
        {
            var any = false;
            OnDisk(folder => any = Directory.EnumerateFiles(folder, FilePattern).Any());
            return any;
        }
    }

    /// <summary>Keeps <paramref name="json"/> as the answer to <paramref name="question"/>, unless something was dropped since <paramref name="generation"/>.</summary>
    public void Keep(string question, string json, int generation)
    {
        ArgumentNullException.ThrowIfNull(json);
        var key = KeyOf(question);
        var answer = new KnownAnswer(json, _time.GetUtcNow());
        lock (_sync)
        {
            if (generation != _generation)
            {
                return;
            }

            Remember(key, answer);
            OnDisk(folder =>
            {
                Write(Path.Combine(folder, FileOf(key)), key, answer);

                // As many on disk as in memory, give or take what's written between looks.
                if (++_written % PruneEvery == 0)
                {
                    Prune(folder);
                }
            });
        }
    }

    /// <summary>The last good answer to <paramref name="question"/>, or null when there's none recent enough.</summary>
    public KnownAnswer? Find(string question)
    {
        var key = KeyOf(question);
        lock (_sync)
        {
            KnownAnswer? answer = null;
            if (_answers.TryGetValue(key, out var node))
            {
                answer = node.Value.Answer;
                _byUse.Remove(node);
                _byUse.AddFirst(node);
            }
            else
            {
                OnDisk(folder => answer = Read(Path.Combine(folder, FileOf(key)), key));
                if (answer is not null && IsRecent(answer))
                {
                    Remember(key, answer);
                }
            }

            return answer is not null && IsRecent(answer) ? answer : null;
        }
    }

    /// <summary>Drops the answer to <paramref name="question"/>: what it says isn't so any more, or isn't this account's to see.</summary>
    public void Forget(string question)
    {
        var key = KeyOf(question);
        lock (_sync)
        {
            _generation++;
            Remove(key);
            Drop(folder => File.Delete(Path.Combine(folder, FileOf(key))));
        }
    }

    /// <summary>Drops every answer: the token no longer works, so what it once saw isn't for anyone who has it now.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _generation++;
            _answers.Clear();
            _byUse.Clear();
            _characters = 0;
            Drop(folder =>
            {
                foreach (var file in Directory.EnumerateFiles(folder, FilePattern))
                {
                    File.Delete(file);
                }
            });
        }
    }

    private static byte[] Derive(byte[] secret, string use)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Encoding.UTF8.GetBytes("DashyNMS MCP last known good, version 1"), Encoding.UTF8.GetBytes(use));

    private string KeyOf(string question) => Convert.ToHexStringLower(HMACSHA256.HashData(_nameKey, Encoding.UTF8.GetBytes(question)));

    private static string FileOf(string key) => key + FileExtension;

    /// <summary>Neither too old, nor from a clock that has since gone back.</summary>
    private bool IsRecent(KnownAnswer answer)
    {
        var age = _time.GetUtcNow() - answer.AsOf;
        return age >= TimeSpan.FromMinutes(-5) && age < _lifetime;
    }

    private void Remember(string key, KnownAnswer answer)
    {
        Remove(key);
        _answers[key] = _byUse.AddFirst((key, answer));
        _characters += answer.Json.Length;
        while (_byUse.Last is { } last && (_answers.Count > MaxAnswers || _characters > MaxCharacters))
        {
            Remove(last.Value.Key);
        }
    }

    private void Remove(string key)
    {
        if (_answers.Remove(key, out var node))
        {
            _byUse.Remove(node);
            _characters -= node.Value.Answer.Json.Length;
        }
    }

    private void Prepare(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Prune(folder);

        DeleteUnused(new DirectoryInfo(folder));
    }

    /// <summary>
    /// Deletes another token's or build's folder once nothing in it could be
    /// used under any setting. Only one of our own, by its name and by there
    /// being nothing else in it: the operator's folder may hold other things.
    /// </summary>
    private void DeleteUnused(DirectoryInfo folder)
    {
        var unused = _time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(McpOptions.MaximumLastKnownGoodHours);
        foreach (var other in folder.Parent!.EnumerateDirectories(FolderPrefix + "*").Where(d => d.Name != folder.Name).ToList())
        {
            try
            {
                var files = other.EnumerateFileSystemInfos().ToList();
                if (other.LastWriteTimeUtc < unused && files.All(f => f is FileInfo && f.Name.Contains(FileExtension, StringComparison.Ordinal) && f.LastWriteTimeUtc < unused))
                {
                    files.ForEach(f => f.Delete());
                    other.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not this server's to delete, and no reason not to use its own.
            }
        }
    }

    /// <summary>Deletes what's too old, or more than is kept: it won't be used again.</summary>
    private void Prune(string folder)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var (file, index) in new DirectoryInfo(folder).EnumerateFiles(FilePattern).OrderByDescending(f => f.LastWriteTimeUtc).Select((f, i) => (f, i)).ToList())
        {
            if (index >= MaxAnswers || now - file.LastWriteTimeUtc >= _lifetime)
            {
                file.Delete();
            }
        }
    }

    private void Write(string path, string key, KnownAnswer answer)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(answer);
        var sealedBytes = new byte[NonceBytes + plain.Length + TagBytes];
        RandomNumberGenerator.Fill(sealedBytes.AsSpan(0, NonceBytes));
        using (var aes = new AesGcm(_fileKey, TagBytes))
        {
            aes.Encrypt(
                sealedBytes.AsSpan(0, NonceBytes),
                plain,
                sealedBytes.AsSpan(NonceBytes, plain.Length),
                sealedBytes.AsSpan(NonceBytes + plain.Length),
                Encoding.UTF8.GetBytes(key));
        }

        // Beside it first, so a file is only ever whole. The name is its own, as two calls may answer one question at once.
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(partial, options))
            {
                stream.Write(sealedBytes);
            }

            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    /// <summary>The answer in a file, or null when there's none, or it isn't one this account and configuration wrote.</summary>
    private KnownAnswer? Read(string path, string key)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var sealedBytes = File.ReadAllBytes(path);
        if (sealedBytes.Length < NonceBytes + TagBytes)
        {
            return null;
        }

        var plain = new byte[sealedBytes.Length - NonceBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(_fileKey, TagBytes);
            aes.Decrypt(
                sealedBytes.AsSpan(0, NonceBytes),
                sealedBytes.AsSpan(NonceBytes, plain.Length),
                sealedBytes.AsSpan(NonceBytes + plain.Length),
                plain,
                Encoding.UTF8.GetBytes(key));
            return JsonSerializer.Deserialize<KnownAnswer>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Deletes from the folder - and when that fails, what should have gone must not be read back, so the folder isn't used again.</summary>
    private void Drop(Action<string> delete)
    {
        if (!OnDisk(delete))
        {
            _folder = null;
        }
    }

    /// <summary>The folder is a convenience: when it can't be used, answers are kept in memory alone, and the log says so once.</summary>
    private bool OnDisk(Action<string> use)
    {
        if (_folder is null)
        {
            return true;
        }

        try
        {
            use(_folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_diskFailed)
            {
                _diskFailed = true;
                _logger.LogWarning("Last known good answers can't be kept in {Setting}, so they're in memory alone: {Reason}", McpOptions.LastKnownGoodDirectoryKey, ex.Message);
            }

            return false;
        }
    }
}
