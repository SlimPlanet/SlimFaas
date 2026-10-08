using System.Collections.Concurrent;

namespace SlimFaas.Security;

/// <summary>Keys accepted for a caller: the current key and, during rotation, the next one.</summary>
internal interface ICallerKeyStore
{
    /// <summary>
    /// Returns the keys of <paramref name="callerId"/>, or an empty list when the caller is
    /// unknown. <paramref name="callerId"/> must already satisfy
    /// <see cref="RequestSignature.IsValidCallerId"/>.
    /// </summary>
    IReadOnlyList<byte[]> GetKeys(string callerId);
}

/// <summary>
/// File-backed key store: <c>&lt;directory&gt;/&lt;caller-id&gt;</c> and
/// <c>&lt;directory&gt;/&lt;caller-id&gt;.next</c>. Files are re-read when their size or
/// last-write time changes, checked at most every <c>KeyRefreshSeconds</c>, so a
/// Kubernetes Secret update (atomic symlink swap) is picked up without a restart.
/// Trailing whitespace is stripped; a key shorter than
/// <see cref="CallerAuthenticationOptions.MinimumKeyBytes"/> is ignored.
/// </summary>
internal sealed class FileCallerKeyStore(string directory, TimeSpan refreshInterval, TimeProvider? timeProvider = null)
    : ICallerKeyStore
{
    private static readonly string[] s_suffixes = ["", ".next"];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public string Directory { get; } = directory;

    public IReadOnlyList<byte[]> GetKeys(string callerId)
    {
        long now = _timeProvider.GetTimestamp();
        if (_cache.TryGetValue(callerId, out CacheEntry? entry)
            && _timeProvider.GetElapsedTime(entry.CheckedAt, now) < refreshInterval)
        {
            return entry.Keys;
        }

        List<FileStamp> stamps = [];
        foreach (string suffix in s_suffixes)
        {
            stamps.Add(FileStamp.Of(Path.Combine(Directory, callerId + suffix)));
        }

        if (entry is not null && entry.Stamps.SequenceEqual(stamps))
        {
            entry = new CacheEntry(entry.Keys, entry.Stamps, now);
            _cache[callerId] = entry;
            return entry.Keys;
        }

        List<byte[]> keys = [];
        foreach (FileStamp stamp in stamps)
        {
            byte[]? key = ReadKey(stamp);
            if (key is not null)
            {
                keys.Add(key);
            }
        }

        entry = new CacheEntry(keys, stamps, now);
        _cache[callerId] = entry;
        return entry.Keys;
    }

    private static byte[]? ReadKey(FileStamp stamp)
    {
        if (!stamp.Exists)
        {
            return null;
        }

        byte[] content;
        try
        {
            content = File.ReadAllBytes(stamp.Path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        int length = content.Length;
        while (length > 0 && content[length - 1] is (byte)'\n' or (byte)'\r' or (byte)' ' or (byte)'\t')
        {
            length--;
        }

        if (length < CallerAuthenticationOptions.MinimumKeyBytes)
        {
            return null;
        }

        return length == content.Length ? content : content[..length];
    }

    private sealed record CacheEntry(IReadOnlyList<byte[]> Keys, IReadOnlyList<FileStamp> Stamps, long CheckedAt);

    private readonly record struct FileStamp(string Path, bool Exists, long Length, DateTime LastWriteUtc)
    {
        public static FileStamp Of(string path)
        {
            FileInfo info = new(path);
            return info.Exists
                ? new FileStamp(path, true, info.Length, info.LastWriteTimeUtc)
                : new FileStamp(path, false, 0, DateTime.MinValue);
        }
    }
}
