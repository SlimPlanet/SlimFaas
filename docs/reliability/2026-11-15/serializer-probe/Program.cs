using System.Globalization;
using System.Security.Cryptography;
using MemoryPack;
using SlimData;

string mode = args.Single();
if (mode is not ("legacy" or "exact"))
    return 2;
Console.WriteLine(FormattableString.Invariant($"mode={mode};heapLimit={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}"));
var response = new SlimDataCommandBatchResponse
{
    ProducerId = "bounded-memory-probe",
    Sequence = 1,
    RequestId = "response",
    Results = [new SlimDataBatchOperationResult
    {
        Kind = SlimDataBatchOperationKind.ListRightPop,
        QueueItems = Enumerable.Range(0, 64).Select(static index => new QueueData(
            index.ToString(CultureInfo.InvariantCulture), Value(index), 0, false, 0, 0)).ToArray()
    }]
};
try
{
    byte[] encoded = mode == "legacy"
        ? MemoryPackSerializer.Serialize(response)
        : SlimDataCommandBatchResponseCodec.Serialize(response);
    Console.WriteLine(FormattableString.Invariant($"bytes={encoded.Length};sha256={Convert.ToHexString(SHA256.HashData(encoded))};heap={GC.GetTotalMemory(false)}"));
    GC.KeepAlive(response);
    return 0;
}
catch (OutOfMemoryException)
{
    Console.WriteLine("OutOfMemoryException");
    GC.KeepAlive(response);
    return 3;
}

static byte[] Value(int index)
{
    byte[] value = new byte[2 * 1024 * 1024];
    value[0] = (byte)index;
    value[^1] = (byte)(index ^ 0x5a);
    return value;
}
