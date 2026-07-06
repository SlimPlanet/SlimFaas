using MemoryPack;

namespace SlimData.Commands;

public enum KeyValueOperation : byte
{
    Set = 0,
    IncrementInteger = 1,
    IncrementFloat = 2
}

public enum KeyValueCommandStatus : byte
{
    None = 0,
    Applied = 1,
    InvalidNumber = 2,
    Overflow = 3,
    NotCommitted = 4
}

[MemoryPackable]
public partial class KeyValueCommandResult
{
    public KeyValueCommandStatus Status { get; set; } = KeyValueCommandStatus.None;
    public byte[]? Value { get; set; }
    public long? IntegerValue { get; set; }
    public decimal? DecimalValue { get; set; }
    public string? ErrorMessage { get; set; }

    [MemoryPackIgnore]
    public bool Applied => Status == KeyValueCommandStatus.Applied;

    public void SetApplied(ReadOnlyMemory<byte> value, long? integerValue = null, decimal? decimalValue = null)
    {
        Status = KeyValueCommandStatus.Applied;
        Value = value.ToArray();
        IntegerValue = integerValue;
        DecimalValue = decimalValue;
        ErrorMessage = null;
    }

    public void SetError(KeyValueCommandStatus status, string message)
    {
        Status = status;
        Value = null;
        IntegerValue = null;
        DecimalValue = null;
        ErrorMessage = message;
    }
}
