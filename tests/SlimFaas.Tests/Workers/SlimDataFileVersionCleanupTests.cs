using System.Collections.Immutable;
using DotNext;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Moq;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimData.Expiration;
using SlimFaas;

namespace SlimFaas.Tests.Workers;

public sealed class SlimDataFileVersionCleanupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "slimfaas_version_cleanup_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Cleanup_preserves_a_live_durable_reference_after_the_local_upload_ttl_has_elapsed()
    {
        var repository = CreateRepository();
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        FilePutResult put = await repository.SaveAsync("file", content, "text/plain", false,
            DateTime.UtcNow.AddSeconds(-1).Ticks, CancellationToken.None);
        SlimDataPayload payload = Referencing(put);
        payload.KeyValues = payload.KeyValues.Add(SlimDataInterpreter.TtlKey(DataFileKeys.MetaKey("file")),
            BitConverter.GetBytes(DateTime.UtcNow.AddMinutes(1).Ticks));
        var state = new Mock<ISupplier<SlimDataPayload>>();
        state.Setup(value => value.Invoke()).Returns(payload);
        var cleaner = new SlimDataExpirationCleaner(state.Object, Mock.Of<IDatabaseService>(), repository,
            Mock.Of<ILogger<SlimDataExpirationCleaner>>());

        await cleaner.CleanupOnceAsync(CancellationToken.None);

        Assert.True(await repository.ExistsAsync("file", put.Sha256Hex, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_retires_only_the_revision_without_a_durable_reference(bool referenceReplacement)
    {
        var repository = CreateRepository();
        using var original = new MemoryStream(new byte[] { 1, 2, 3 });
        FilePutResult before = await repository.SaveAsync("file", original, "text/plain", false, null, CancellationToken.None);
        using var replacement = new MemoryStream(new byte[] { 4, 5, 6 });
        FilePutResult after = await repository.SaveAsync("file", replacement, "text/plain", true, null, CancellationToken.None);
        FilePutResult kept = referenceReplacement ? after : before;
        FilePutResult retired = referenceReplacement ? before : after;
        var state = new Mock<ISupplier<SlimDataPayload>>();
        state.Setup(value => value.Invoke()).Returns(Referencing(kept));
        var cleaner = new SlimDataExpirationCleaner(state.Object, Mock.Of<IDatabaseService>(), repository,
            Mock.Of<ILogger<SlimDataExpirationCleaner>>());

        for (int cycle = 0; cycle < 3; cycle++)
            await cleaner.CleanupOnceAsync(CancellationToken.None);

        Assert.True(await repository.ExistsAsync("file", kept.Sha256Hex, CancellationToken.None));
        Assert.False(await repository.ExistsAsync("file", retired.Sha256Hex, CancellationToken.None));
    }

    private DiskFileRepository CreateRepository() =>
        new(_directory, Mock.Of<ILogger<DiskFileRepository>>(), dropPageCache: false);

    private static SlimDataPayload Referencing(FilePutResult put) => new()
    {
        KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add(DataFileKeys.MetaKey("file"),
            MemoryPackSerializer.Serialize(new DataSetMetadata(put.Sha256Hex, put.Length, put.ContentType, "file"))),
        Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
    };
}
