using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimData.ClusterFiles;
using SlimData.ClusterFiles.Http;
using Xunit;

public sealed class ClusterFileTransferRoutesTests
{
    // ⚠️ Adapte ce type si nécessaire (ex: ClusterFileMetadata, FileRepoMetadata, etc.)
    // Il doit correspondre exactement au type retourné par IFileRepository.TryGetMetadataAsync(...)
    private static FileMetadata Meta(
        string sha,
        long length = 4,
        string? contentType = "text/plain",
        long? expireAtUtcTicks = 123)
        => new FileMetadata(
            contentType,
            sha,
            length,
             expireAtUtcTicks
        );

    private static async Task<WebApplication> CreateAppAsync(Mock<IFileRepository> repoMock)
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton<IFileRepository>(repoMock.Object);
        builder.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        var app = builder.Build();
        app.MapClusterFileTransferRoutes();

        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Head_InvalidId_returns_400()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);

        await using var app = await CreateAppAsync(repo);
        var client = app.GetTestClient();

        var req = new HttpRequestMessage(HttpMethod.Head, "/cluster/files/bad%20id?sha=abc");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Head_MissingSha_returns_400()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);

        await using var app = await CreateAppAsync(repo);
        var client = app.GetTestClient();

        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/cluster/files/id1"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Head_MetadataNotFound_returns_404()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        repo.Setup(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);

        await using var app = await CreateAppAsync(repo);
        var client = app.GetTestClient();

        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/cluster/files/id1?sha=deadbeef"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        repo.Verify(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Head_ShaMismatch_returns_404()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        repo.Setup(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Meta("aaaaaaaa"));

        await using var app = await CreateAppAsync(repo);
        var client = app.GetTestClient();

        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/cluster/files/id1?sha=bbbbbbbb"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        repo.Verify(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Head_Ok_sets_headers_and_returns_200()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        repo.Setup(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Meta("deadbeef", length: 42, contentType: "application/pdf", expireAtUtcTicks: 999));

        await using var app = await CreateAppAsync(repo);
        var client = app.GetTestClient();

        // sha en MAJUSCULE pour valider le case-insensitive
        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/cluster/files/id1?sha=DEADBEEF"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.True(resp.Headers.TryGetValues("Accept-Ranges", out var ar));
        Assert.Equal("bytes", Assert.Single(ar));

        Assert.True(resp.Headers.TryGetValues("ETag", out var etags));
        Assert.Equal("\"deadbeef\"", Assert.Single(etags));

        Assert.True(resp.Headers.TryGetValues("X-SlimFaas-ExpireAtUtcTicks", out var exp));
        Assert.Equal("999", Assert.Single(exp));

        Assert.Equal(42, resp.Content.Headers.ContentLength);
        Assert.Equal("application/pdf", resp.Content.Headers.ContentType?.MediaType);

        repo.Verify(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Head_ContentTypeNull_defaults_to_octet_stream()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        repo.Setup(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Meta("deadbeef", length: 1, contentType: null, expireAtUtcTicks: null));

        await using var app = await CreateAppAsync(repo);
        var client = app.GetTestClient();

        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/cluster/files/id1?sha=deadbeef"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(MediaTypeNames.Application.Octet, resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_InvalidId_returns_400()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);

        await using var app = await CreateAppAsync(repo);
        var client = app.GetTestClient();

        var resp = await client.GetAsync("/cluster/files/bad%20id?sha=abc");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Get_MetadataNotFound_returns_404()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        repo.Setup(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);

        await using var app = await CreateAppAsync(repo);
        var client = app.GetTestClient();

        var resp = await client.GetAsync("/cluster/files/id1?sha=deadbeef");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        repo.Verify(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Get_ShaMismatch_returns_404()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        repo.Setup(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Meta("aaaaaaaa"));

        await using var app = await CreateAppAsync(repo);
        var client = app.GetTestClient();

        var resp = await client.GetAsync("/cluster/files/id1?sha=bbbbbbbb");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        repo.Verify(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Get_Ok_streams_file_and_sets_headers()
    {
        var payload = new byte[] { 1, 2, 3, 4 };

        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        repo.Setup(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Meta("deadbeef", length: payload.Length, contentType: "text/plain", expireAtUtcTicks: 777));

        repo.Setup(r => r.OpenReadAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(payload));

        await using var app = await CreateAppAsync(repo);
        var client = app.GetTestClient();

        var resp = await client.GetAsync("/cluster/files/id1?sha=deadbeef");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.True(resp.Headers.TryGetValues("Accept-Ranges", out var ar));
        Assert.Equal("bytes", Assert.Single(ar));

        Assert.True(resp.Headers.TryGetValues("ETag", out var etags));
        Assert.Equal("\"deadbeef\"", Assert.Single(etags));

        Assert.True(resp.Headers.TryGetValues("X-SlimFaas-ExpireAtUtcTicks", out var exp));
        Assert.Equal("777", Assert.Single(exp));

        Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, body);

        repo.Verify(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.OpenReadAsync("id1", It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Get_RangeRequest_returns_206_and_single_byte()
    {
        var payload = new byte[] { 9, 8, 7, 6 };

        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        repo.Setup(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Meta("deadbeef", length: payload.Length, contentType: "application/octet-stream", expireAtUtcTicks: null));

        // stream seekable indispensable pour le range processing
        repo.Setup(r => r.OpenReadAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(payload));

        await using var app = await CreateAppAsync(repo);
        var client = app.GetTestClient();

        var req = new HttpRequestMessage(HttpMethod.Get, "/cluster/files/id1?sha=deadbeef");
        req.Headers.Range = new RangeHeaderValue(0, 0);

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Single(bytes);
        Assert.Equal(9, bytes[0]);

        repo.Verify(r => r.TryGetMetadataAsync("id1", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.OpenReadAsync("id1", It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();
    }
}
