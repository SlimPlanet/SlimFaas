using System.Security.Cryptography;
using MemoryPack;
using SlimFaas;

try
{
    bool borrow = args.Length == 1 && args[0] == "borrow";
    byte[][] packets = CreatePackets();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    var decoded = new (CustomRequest Request, Stream? BodyStream)[packets.Length];
    for (var index = 0; index < packets.Length; index++)
    {
        decoded[index] = borrow
            ? QueuedHttpRequestCodec.DeserializeForDispatch(packets[index])
            : (MemoryPackSerializer.Deserialize<CustomRequest>(packets[index]), null);
    }
    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    long bytes = 0;
    foreach (var item in decoded)
    {
        using Stream? bodyStream = item.BodyStream;
        byte[] bodyHash;
        if (bodyStream is not null)
        {
            bytes += bodyStream.Length;
            bodyHash = SHA256.HashData(bodyStream);
        }
        else
        {
            byte[] body = item.Request.Body!;
            bytes += body.Length;
            bodyHash = SHA256.HashData(body);
        }
        digest.AppendData(bodyHash);
    }
    Console.WriteLine(FormattableString.Invariant($"bytes={bytes};sha256={Convert.ToHexString(digest.GetHashAndReset())};heap={GC.GetTotalMemory(false)}"));
    GC.KeepAlive(packets);
    GC.KeepAlive(decoded);
    return 0;
}
catch (OutOfMemoryException)
{
    Console.WriteLine("OutOfMemoryException");
    return 3;
}

static byte[][] CreatePackets()
{
    byte[] body = new byte[2 * 1024 * 1024];
    for (var index = 0; index < body.Length; index++)
        body[index] = (byte)(index % 251);
    var packets = new byte[96][];
    var request = new CustomRequest([new("Content-Type", ["application/octet-stream"])], body,
        "memory-probe", "/work", "POST", "?value=\u00e9");
    for (var index = 0; index < packets.Length; index++)
        packets[index] = MemoryPackSerializer.Serialize(request);
    return packets;
}
