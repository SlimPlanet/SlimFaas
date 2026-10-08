using System.Text;
using SlimFaas.Security;

namespace SlimFaas.Tests.Security;

public sealed class CallerKeyStoreAndNonceCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "slimfaas-keys-" + Guid.NewGuid().ToString("N"));

    public CallerKeyStoreAndNonceCacheTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void KeyStore_ReadsCurrentAndNextKeys_AndStripsTrailingWhitespace()
    {
        File.WriteAllText(Path.Combine(_directory, "billing-api"), "0123456789abcdef0123456789abcdef\n");
        File.WriteAllText(Path.Combine(_directory, "billing-api.next"), "next-key-next-key-next-key-next!\r\n");
        FileCallerKeyStore store = new(_directory, TimeSpan.FromSeconds(10));

        IReadOnlyList<byte[]> keys = store.GetKeys("billing-api");

        Assert.Equal(2, keys.Count);
        Assert.Equal("0123456789abcdef0123456789abcdef", Encoding.ASCII.GetString(keys[0]));
        Assert.Equal("next-key-next-key-next-key-next!", Encoding.ASCII.GetString(keys[1]));
    }

    [Fact]
    public void KeyStore_UnknownCallerAndShortKeys_YieldNoKey()
    {
        File.WriteAllText(Path.Combine(_directory, "weak-api"), "too-short");
        FileCallerKeyStore store = new(_directory, TimeSpan.FromSeconds(10));

        Assert.Empty(store.GetKeys("nobody"));
        Assert.Empty(store.GetKeys("weak-api"));
    }

    [Fact]
    public void KeyStore_PicksUpAChangedFileAfterTheRefreshInterval()
    {
        string path = Path.Combine(_directory, "billing-api");
        File.WriteAllText(path, "0123456789abcdef0123456789abcdef");
        ManualTimeProvider time = new();
        FileCallerKeyStore store = new(_directory, TimeSpan.FromSeconds(10), time);
        Assert.Single(store.GetKeys("billing-api"));

        File.WriteAllText(path, "rotated-key-rotated-key-rotated!");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Equal("0123456789abcdef0123456789abcdef", Encoding.ASCII.GetString(store.GetKeys("billing-api")[0]));
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal("rotated-key-rotated-key-rotated!", Encoding.ASCII.GetString(store.GetKeys("billing-api")[0]));
    }

    [Fact]
    public void NonceCache_RejectsReplayWithinTheWindow_AndAcceptsAfterExpiry()
    {
        ManualTimeProvider time = new();
        NonceCache cache = new(TimeSpan.FromSeconds(600), 10, time);

        Assert.True(cache.TryAdd("billing-api", "n1"));
        Assert.False(cache.TryAdd("billing-api", "n1"));
        Assert.True(cache.TryAdd("other-api", "n1"));

        time.Advance(TimeSpan.FromSeconds(601));
        Assert.True(cache.TryAdd("billing-api", "n1"));
    }

    [Fact]
    public void NonceCache_FailsClosedWhenFull_AndRecoversWhenEntriesExpire()
    {
        ManualTimeProvider time = new();
        NonceCache cache = new(TimeSpan.FromSeconds(600), 3, time);

        Assert.True(cache.TryAdd("billing-api", "n1"));
        Assert.True(cache.TryAdd("billing-api", "n2"));
        Assert.True(cache.TryAdd("billing-api", "n3"));
        Assert.False(cache.TryAdd("billing-api", "n4"));

        time.Advance(TimeSpan.FromSeconds(601));
        Assert.True(cache.TryAdd("billing-api", "n4"));
    }

    [Fact]
    public void LogThrottle_LetsOneMessagePerKeyPerInterval()
    {
        ManualTimeProvider time = new();
        LogThrottle throttle = new(TimeSpan.FromSeconds(60), timeProvider: time);

        Assert.True(throttle.ShouldLog("a"));
        Assert.False(throttle.ShouldLog("a"));
        Assert.True(throttle.ShouldLog("b"));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(throttle.ShouldLog("a"));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = 1_000_000;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => 1_000;

        public void Advance(TimeSpan by) => _timestamp += (long)(by.TotalSeconds * TimestampFrequency);
    }
}
