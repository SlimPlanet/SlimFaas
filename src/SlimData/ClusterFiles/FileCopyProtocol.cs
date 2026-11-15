using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNext.IO;
using DotNext.Net.Cluster.Messaging;

namespace SlimData.ClusterFiles;

public sealed record FileCopyRequest(string Id, string Sha256Hex, long Length, string SourceStorageIdentity);
public sealed record FileCopyReceipt(string Id, string Sha256Hex, long Length, string NodeIdentity,
    string StorageIdentity, bool Persisted, string? Error = null);

[JsonSerializable(typeof(FileCopyRequest))]
[JsonSerializable(typeof(FileCopyReceipt))]
internal partial class FileCopyJsonContext : JsonSerializerContext;

internal static class FileCopyProtocol
{
    internal const string RequestName = "slimfaas.file.persist.v1";
    internal const string ReceiptName = "slimfaas.file.persisted.v1";
    private const long MaximumMessageBytes = 4096;

    internal static TextMessage Request(FileCopyRequest request) =>
        new(JsonSerializer.Serialize(request, FileCopyJsonContext.Default.FileCopyRequest), RequestName);

    internal static TextMessage Reply(FileCopyReceipt receipt) =>
        new(JsonSerializer.Serialize(receipt, FileCopyJsonContext.Default.FileCopyReceipt), ReceiptName);

    internal static async Task<FileCopyRequest> ReadRequestAsync(IMessage message, CancellationToken ct)
    {
        ValidateEnvelope(message, RequestName);
        string json = await message.ToStringAsync(Encoding.UTF8, token: ct).ConfigureAwait(false);
        FileCopyRequest request = JsonSerializer.Deserialize(json, FileCopyJsonContext.Default.FileCopyRequest)
            ?? throw new InvalidDataException("Missing file copy request.");
        if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 1024 || request.Length < 0 ||
            request.Sha256Hex is null || request.Sha256Hex.Length != 64 ||
            !Guid.TryParseExact(request.SourceStorageIdentity, "N", out Guid storage) || storage == Guid.Empty)
            throw new InvalidDataException("Invalid file copy identity, length or storage identity.");
        foreach (char value in request.Sha256Hex)
            if (!char.IsAsciiHexDigit(value))
                throw new InvalidDataException("Invalid file copy checksum.");
        return request;
    }

    internal static async Task<FileCopyReceipt> ReadReceiptAsync(IMessage message, CancellationToken ct)
    {
        ValidateEnvelope(message, ReceiptName);
        string json = await message.ToStringAsync(Encoding.UTF8, token: ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, FileCopyJsonContext.Default.FileCopyReceipt)
            ?? throw new InvalidDataException("Missing file copy receipt.");
    }

    internal static bool Matches(FilePersistenceReceipt local, string expectedNode, FileCopyReceipt remote) =>
        remote.Persisted && remote.Error is null && remote.Id == local.Id &&
        string.Equals(remote.Sha256Hex, local.Sha256Hex, StringComparison.OrdinalIgnoreCase) &&
        remote.Length == local.Length && remote.NodeIdentity == expectedNode &&
        Guid.TryParseExact(remote.StorageIdentity, "N", out Guid volume) && volume != Guid.Empty &&
        !string.Equals(remote.StorageIdentity, local.StorageIdentity, StringComparison.OrdinalIgnoreCase);

    private static void ValidateEnvelope(IMessage message, string expectedName)
    {
        if (message.Name != expectedName || message.Length is not long length || length is < 1 or > MaximumMessageBytes)
            throw new InvalidDataException("Invalid or oversized file copy message.");
    }
}
