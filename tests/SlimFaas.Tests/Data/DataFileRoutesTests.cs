using System.Collections.Immutable;
using System.Text;
using DotNext;
using MemoryPack;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimFaas;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;

public sealed class DataFileRoutesTests
{
    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------
    private static DefaultHttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();

        // CRITICAL: IResult.ExecuteAsync() requires RequestServices for many built-in results.
        ctx.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<(int StatusCode, string? ContentType, byte[] Body)> ExecuteAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var bytes = ((MemoryStream)ctx.Response.Body).ToArray();
        return (ctx.Response.StatusCode, ctx.Response.ContentType, bytes);
    }


        [Fact]
    public void ListFilesAsync_returns_file_ids_and_expiration()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);

        var ticks1 = DateTime.UtcNow.AddMinutes(10).Ticks;

        var kv = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add("data:file:abc:meta", new byte[] { 0x01 })
            .Add("data:file:abc:meta${slimfaas-timetolive}$", BitConverter.GetBytes(ticks1))
            .Add("data:file:no-ttl:meta", new byte[] { 0x02 }) // pas de ttl => expiration null
            .Add("something:else", new byte[] { 0xFF });       // ignoré

        var data = new SlimDataPayload
        {
            KeyValues = kv,
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        };

        state.Setup(s => s.Invoke()).Returns(data);

        var result = DataFileRoutes.DataFileHandlers.ListFilesAsync(state.Object);

        var ok = Assert.IsType<Ok<List<DataFileEntry>>>(result);
        Assert.NotNull(ok.Value);

        var list = ok.Value!;
        Assert.Equal(2, list.Count);

        var abc = Assert.Single(list, x => x.Id == "abc");
        Assert.Equal(ticks1, abc.ExpireAtUtcTicks);

        var noTtl = Assert.Single(list, x => x.Id == "no-ttl");
        Assert.Equal(-1, noTtl.ExpireAtUtcTicks);
    }

    [Fact]
    public void ListFilesAsync_sorts_by_expiration_then_id()
    {
        var state = new Mock<ISupplier<SlimDataPayload>>(MockBehavior.Strict);

        var t1 = DateTime.UtcNow.AddMinutes(5).Ticks;
        var t2 = DateTime.UtcNow.AddMinutes(15).Ticks;

        var kv = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
            .Add("data:file:b:meta", new byte[] { 0x01 })
            .Add("data:file:b:meta${slimfaas-timetolive}$", BitConverter.GetBytes(t2))
            .Add("data:file:a:meta", new byte[] { 0x01 })
            .Add("data:file:a:meta${slimfaas-timetolive}$", BitConverter.GetBytes(t1));

        var data = new SlimDataPayload
        {
            KeyValues = kv,
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        };

        state.Setup(s => s.Invoke()).Returns(data);

        var result = DataFileRoutes.DataFileHandlers.ListFilesAsync(state.Object);
        var ok = Assert.IsType<Ok<List<DataFileEntry>>>(result);

        var list = ok.Value!;
        Assert.Equal(2, list.Count);
        Assert.Equal("b", list[0].Id); // t1
        Assert.Equal("a", list[1].Id); // t2
    }

    // ------------------------------------------------------------
    // POST tests
    // ------------------------------------------------------------
    [Fact]
    public async Task Post_RawBody_GeneratesGuid_WhenKeyMissing_AndStoresMemoryPackMetadata()
    {
        // Arrange
        var fileSync = new Mock<IClusterFileSync>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var payload = Encoding.UTF8.GetBytes("hello");
        var ctx = NewHttpContext();
        ctx.Request.ContentType = "application/octet-stream";
        ctx.Request.Body = new MemoryStream(payload);
        ctx.Request.Headers["Content-Disposition"] = "attachment; filename=\"a.bin\"";

        string? capturedId = null;
        string? capturedContentType = null;

        fileSync.Setup(s => s.BroadcastFilePutAsync(
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                true,
                It.IsAny<CancellationToken>()))
            .Callback<string, Stream, string, bool, CancellationToken>((id, _, ctType, _, _) =>
            {
                capturedId = id;
                capturedContentType = ctType;
            })
            .ReturnsAsync(new FilePutResult("sha1", "application/octet-stream", payload.Length));

        byte[]? storedMetaBytes = null;
        string? storedMetaKey = null;
        long? storedTtl = null;

        db.Setup(d => d.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<long?>()))
          .Callback<string, byte[], long?>((k, v, ttl) =>
          {
              storedMetaKey = k;
              storedMetaBytes = v;
              storedTtl = ttl;
          })
          .Returns(Task.CompletedTask);

        var ttlMs = 12345L;

        // Act
        var result = await DataFileRoutes.DataFileHandlers.PostAsync(
            ctx, id: null, ttl: ttlMs, fileSync.Object, db.Object, CancellationToken.None);

        var (status, _, bodyBytes) = await ExecuteAsync(result, ctx);
        var elementId = Encoding.UTF8.GetString(bodyBytes);

        // Assert
        Assert.Equal(200, status);
        Assert.False(string.IsNullOrWhiteSpace(elementId));
        Assert.Equal(capturedId, elementId);
        Assert.Equal("application/octet-stream", capturedContentType);

        Assert.Equal($"data:file:{elementId}:meta", storedMetaKey);
        Assert.Equal(ttlMs, storedTtl);

        var meta = MemoryPackSerializer.Deserialize<DataSetMetadata>(storedMetaBytes!);
        Assert.Equal("sha1", meta.Sha256Hex);
        Assert.Equal(payload.Length, meta.Length);
        Assert.Equal("application/octet-stream", meta.ContentType);
        Assert.Equal("a.bin", meta.FileName);

        fileSync.VerifyAll();
        db.VerifyAll();
    }

    [Fact]
    public async Task Post_Multipart_UsesFormFileName_AndFormFileContentType()
    {
        // Arrange
        var fileSync = new Mock<IClusterFileSync>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var bytes = Encoding.UTF8.GetBytes("content");
        var ctx = NewHttpContext();
        ctx.Request.ContentType = "multipart/form-data; boundary=----test";

        // Fake IFormFile
        var fileStream = new MemoryStream(bytes);
        var formFile = new FormFile(fileStream, 0, bytes.Length, "file", "hello.txt")
        {
            Headers = new HeaderDictionary
            {
                ["Content-Type"] = new StringValues("text/plain"),
                ["Content-Disposition"] = new StringValues("form-data; name=\"file\"; filename=\"hello.txt\"")
            }
        };

        // IMPORTANT: Don’t set ctx.Request.Form directly; use IFormFeature for ReadFormAsync().
        var formCollection = new FormCollection(
            new Dictionary<string, StringValues>(),
            new FormFileCollection { formFile });

        ctx.Features.Set<IFormFeature>(new FormFeature(formCollection));

        string? capturedContentType = null;

        fileSync.Setup(s => s.BroadcastFilePutAsync(
                "id1",
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                true,
                It.IsAny<CancellationToken>()))
            .Callback<string, Stream, string, bool, CancellationToken>((_, __, ctType, _, _) =>
            {
                capturedContentType = ctType;
            })
            .ReturnsAsync(new FilePutResult("sha2", "text/plain", bytes.Length));

        byte[]? storedMetaBytes = null;
        db.Setup(d => d.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<long?>()))
          .Callback<string, byte[], long?>((_, v, __) => storedMetaBytes = v)
          .Returns(Task.CompletedTask);

        // Act
        var result = await DataFileRoutes.DataFileHandlers.PostAsync(
            ctx, id: "id1", null, fileSync.Object, db.Object, CancellationToken.None);

        var (status, _, bodyBytes) = await ExecuteAsync(result, ctx);
        var elementId = Encoding.UTF8.GetString(bodyBytes);

        // Assert
        Assert.Equal(200, status);
        Assert.Equal("id1", elementId);
        Assert.Equal("text/plain", capturedContentType);

        var meta = MemoryPackSerializer.Deserialize<DataSetMetadata>(storedMetaBytes!);
        Assert.Equal("sha2", meta.Sha256Hex);
        Assert.Equal(bytes.Length, meta.Length);
        Assert.Equal("text/plain", meta.ContentType);
        Assert.Equal("hello.txt", meta.FileName);

        fileSync.VerifyAll();
        db.VerifyAll();
    }

    // ------------------------------------------------------------
    // GET tests
    // ------------------------------------------------------------
    [Fact]
    public async Task Get_ReturnsNotFound_WhenMetadataMissing()
    {
        var fileSync = new Mock<IClusterFileSync>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        db.Setup(d => d.GetAsync("data:file:id1:meta"))
          .ReturnsAsync((byte[]?)null);

        var result = await DataFileRoutes.DataFileHandlers.GetAsync("id1", fileSync.Object, db.Object, CancellationToken.None);

        var ctx = NewHttpContext();
        var (status, _, _) = await ExecuteAsync(result, ctx);

        Assert.Equal(404, status);

        db.VerifyAll();
        fileSync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Get_PullsIfMissing_AndReturnsFile()
    {
        var fileSync = new Mock<IClusterFileSync>(MockBehavior.Strict);
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        var meta = new DataSetMetadata("sha", 5, "application/octet-stream", "x.bin");
        var metaBytes = MemoryPackSerializer.Serialize(meta);

        db.Setup(d => d.GetAsync("data:file:id1:meta"))
          .ReturnsAsync(metaBytes);

        var payload = Encoding.UTF8.GetBytes("abcde");
        fileSync.Setup(s => s.PullFileIfMissingAsync("id1", "sha", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FilePullResult(new MemoryStream(payload)));

        var result = await DataFileRoutes.DataFileHandlers.GetAsync("id1", fileSync.Object, db.Object, CancellationToken.None);

        var ctx = NewHttpContext();
        var (status, contentType, body) = await ExecuteAsync(result, ctx);

        Assert.Equal(200, status);
        Assert.Equal("application/octet-stream", contentType);
        Assert.Equal(payload, body);

        db.VerifyAll();
        fileSync.VerifyAll();
    }

    // ------------------------------------------------------------
    // DELETE tests
    // ------------------------------------------------------------
    [Fact]
    public async Task Delete_DeletesMetadataKey()
    {
        var db = new Mock<IDatabaseService>(MockBehavior.Strict);

        db.Setup(d => d.DeleteAsync("data:file:id1:meta"))
          .Returns(Task.CompletedTask);

        var result = await DataFileRoutes.DataFileHandlers.DeleteAsync("id1", db.Object, CancellationToken.None);

        var ctx = NewHttpContext();
        var (status, _, _) = await ExecuteAsync(result, ctx);

        Assert.Equal(204, status);
        db.VerifyAll();
    }

    private sealed class TestInvocationContext : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; }
        public override IList<object?> Arguments { get; }

        public TestInvocationContext(HttpContext httpContext, params object?[] args)
        {
            HttpContext = httpContext;
            Arguments = args.ToList();
        }

        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    [Fact]
    public async Task DataVisibilityFilter_Allows_WhenDefaultVisibilityPublic()
    {
        var ctx = NewHttpContext();

        var accessPolicy = new Mock<IFunctionAccessPolicy>(MockBehavior.Strict);
        accessPolicy.Setup(p => p.IsInternalRequest(ctx)).Returns(false);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IOptions<DataOptions>>(Options.Create(new DataOptions { DefaultVisibility = FunctionVisibility.Public }))
            .AddSingleton(accessPolicy.Object)
            .BuildServiceProvider();

        var filter = ActivatorUtilities.CreateInstance<DataVisibilityEndpointFilter>(services);

        var inv = new TestInvocationContext(ctx);
        EndpointFilterDelegate next = _ => ValueTask.FromResult<object?>(Results.Ok());

        var output = await filter.InvokeAsync(inv, next);

        Assert.IsAssignableFrom<IResult>(output);
    }

    [Fact]
    public async Task DataVisibilityFilter_Denies_WhenPrivate_AndExternal()
    {
        var ctx = NewHttpContext();

        var accessPolicy = new Mock<IFunctionAccessPolicy>(MockBehavior.Strict);
        accessPolicy.Setup(p => p.IsInternalRequest(ctx)).Returns(false);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IOptions<DataOptions>>(Options.Create(new DataOptions { DefaultVisibility = FunctionVisibility.Private }))
            .AddSingleton(accessPolicy.Object)
            .BuildServiceProvider();

        var filter = ActivatorUtilities.CreateInstance<DataVisibilityEndpointFilter>(services);

        var inv = new TestInvocationContext(ctx);
        EndpointFilterDelegate next = _ => ValueTask.FromResult<object?>(Results.Ok());

        var output = await filter.InvokeAsync(inv, next);
        var result = Assert.IsAssignableFrom<IResult>(output);

        var (status, _, _) = await ExecuteAsync(result, ctx);
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task DataVisibilityFilter_Allows_WhenPrivate_AndInternal()
    {
        var ctx = NewHttpContext();

        var accessPolicy = new Mock<IFunctionAccessPolicy>(MockBehavior.Strict);
        accessPolicy.Setup(p => p.IsInternalRequest(ctx)).Returns(true);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IOptions<DataOptions>>(Options.Create(new DataOptions { DefaultVisibility = FunctionVisibility.Private }))
            .AddSingleton(accessPolicy.Object)
            .BuildServiceProvider();

        var filter = ActivatorUtilities.CreateInstance<DataVisibilityEndpointFilter>(services);

        var inv = new TestInvocationContext(ctx);
        EndpointFilterDelegate next = _ => ValueTask.FromResult<object?>(Results.Ok());

        var output = await filter.InvokeAsync(inv, next);

        Assert.IsAssignableFrom<IResult>(output);
    }


}
