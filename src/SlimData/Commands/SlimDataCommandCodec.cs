using System.Numerics;
using System.Reflection;
using System.Text;
using DotNext.IO;

namespace SlimData.Commands;

public static class SlimDataCommandProtocol
{
    public const string Current = "SLDC/1";
    public const string HeaderName = "X-SlimData-Command-Protocol";
    public const string AssemblyVersionHeaderName = "X-SlimData-Assembly-Version";

    public static string AssemblyVersion { get; } = typeof(SlimDataCommandProtocol).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "unknown";
}

internal enum SlimDataCommandViolation
{
    LegacyFormat,
    UnsupportedVersion,
    InvalidFormat,
    InvalidLength,
    InvalidCount,
    Truncated,
    TooLarge,
    UnknownCommand
}

internal sealed class SlimDataCommandFormatException(
    SlimDataCommandViolation violation,
    string message,
    Exception? innerException = null) : IOException(message, innerException)
{
    internal SlimDataCommandViolation Violation { get; } = violation;
}

internal static class SlimDataCommandCodec
{
    // Little-endian bytes spell "SLDC".
    private const uint Magic = 0x43444C53U;
    private const byte Version = 1;
    internal const int HeaderLength = sizeof(uint) + sizeof(byte);
    internal const int MaxCommandBytes = 32 * 1024 * 1024;
    internal const int MaxStringBytes = 1 * 1024 * 1024;
    internal const int MaxValueBytes = 16 * 1024 * 1024;
    internal const int MaxBatchItems = 1024;
    internal const int MaxCollectionCount = 100_000;

    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static bool IsSupportedCommandId(int? commandId)
        => commandId is AddHashSetCommand.Id
            or AddKeyValueCommand.Id
            or DeleteKeyValueCommand.Id
            or ListLeftPushBatchCommand.Id
            or ListCallbackCommand.Id
            or ListCallbackBatchCommand.Id
            or DeleteHashSetCommand.Id
            or ListRightPopCommand.Id;

    internal static long GetStringLength(string? value)
        => checked(sizeof(int) + Utf8.GetByteCount(value ?? string.Empty));

    internal static long GetBytesLength(int length)
        => checked(GetCompressedLengthSize(length) + length);

    internal static int GetCompressedLengthSize(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var value = (uint)length;
        var result = 1;
        while (value >= 0x80U)
        {
            value >>= 7;
            result++;
        }

        return result;
    }

    internal static void ValidateCommandLength(long length, string commandName)
    {
        if (length < HeaderLength || length > MaxCommandBytes)
        {
            throw new SlimDataCommandFormatException(
                length > MaxCommandBytes
                    ? SlimDataCommandViolation.TooLarge
                    : SlimDataCommandViolation.InvalidLength,
                $"{commandName} serialized length {length} is outside the allowed range " +
                $"{HeaderLength}..{MaxCommandBytes} bytes.");
        }
    }

    internal static void ValidateValueLength(long length, string fieldName)
    {
        if (length < 0L || length > MaxValueBytes)
        {
            throw new SlimDataCommandFormatException(
                length > MaxValueBytes
                    ? SlimDataCommandViolation.TooLarge
                    : SlimDataCommandViolation.InvalidLength,
                $"{fieldName} length {length} is outside the allowed range 0..{MaxValueBytes} bytes.");
        }
    }

    internal static async ValueTask WriteHeaderAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        await writer.WriteLittleEndianAsync(Magic, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(Version, token).ConfigureAwait(false);
    }

    internal static async ValueTask ReadHeaderAsync<TReader>(
        TReader reader,
        string commandName,
        CancellationToken token)
        where TReader : notnull, IAsyncBinaryReader
    {
        ValidateCommandSize(reader, commandName);
        EnsureRemaining(reader, HeaderLength, commandName);

        var magic = await reader.ReadLittleEndianAsync<uint>(token).ConfigureAwait(false);
        if (magic != Magic)
        {
            throw new SlimDataCommandFormatException(
                SlimDataCommandViolation.LegacyFormat,
                $"{commandName} payload does not use the current SlimData command envelope " +
                $"(received magic 0x{magic:X8}, expected 0x{Magic:X8}).");
        }

        var version = await reader.ReadLittleEndianAsync<byte>(token).ConfigureAwait(false);
        if (version != Version)
        {
            throw new SlimDataCommandFormatException(
                SlimDataCommandViolation.UnsupportedVersion,
                $"Unsupported {commandName} envelope version {version}; expected {Version}.");
        }
    }

    internal static async ValueTask WriteStringAsync<TWriter>(
        TWriter writer,
        string? value,
        string fieldName,
        CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        value ??= string.Empty;
        var byteCount = Utf8.GetByteCount(value);
        ValidateLength(byteCount, MaxStringBytes, fieldName);
        await writer.WriteLittleEndianAsync(byteCount, token).ConfigureAwait(false);
        if (byteCount == 0)
            return;

        var bytes = Utf8.GetBytes(value);
        await writer.Invoke(bytes.AsMemory(), token).ConfigureAwait(false);
    }

    internal static async ValueTask<string> ReadStringAsync<TReader>(
        TReader reader,
        string fieldName,
        CancellationToken token)
        where TReader : notnull, IAsyncBinaryReader
    {
        EnsureRemaining(reader, sizeof(int), fieldName);
        var byteCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        ValidateLength(byteCount, MaxStringBytes, fieldName);
        EnsureRemaining(reader, byteCount, fieldName);
        if (byteCount == 0)
            return string.Empty;

        var bytes = GC.AllocateUninitializedArray<byte>(byteCount);
        await reader.ReadAsync(bytes, token).ConfigureAwait(false);
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new SlimDataCommandFormatException(
                SlimDataCommandViolation.InvalidFormat,
                $"{fieldName} is not valid UTF-8.",
                ex);
        }
    }

    internal static async ValueTask WriteBytesAsync<TWriter>(
        TWriter writer,
        ReadOnlyMemory<byte> value,
        string fieldName,
        CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        ValidateLength(value.Length, MaxValueBytes, fieldName);
        await writer.WriteAsync(value, LengthFormat.Compressed, token).ConfigureAwait(false);
    }

    internal static async ValueTask<byte[]> ReadBytesAsync<TReader>(
        TReader reader,
        string fieldName,
        CancellationToken token)
        where TReader : notnull, IAsyncBinaryReader
    {
        var byteCount = await ReadCompressedLengthAsync(reader, fieldName, token).ConfigureAwait(false);
        ValidateLength(byteCount, MaxValueBytes, fieldName);
        EnsureRemaining(reader, byteCount, fieldName);
        if (byteCount == 0)
            return [];

        var result = GC.AllocateUninitializedArray<byte>(byteCount);
        await reader.ReadAsync(result, token).ConfigureAwait(false);
        return result;
    }

    internal static async ValueTask WriteCountAsync<TWriter>(
        TWriter writer,
        int count,
        int maximum,
        string fieldName,
        CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        ValidateCount(count, maximum, fieldName);
        await writer.WriteLittleEndianAsync(count, token).ConfigureAwait(false);
    }

    internal static async ValueTask<int> ReadCountAsync<TReader>(
        TReader reader,
        int maximum,
        string fieldName,
        CancellationToken token)
        where TReader : notnull, IAsyncBinaryReader
    {
        EnsureRemaining(reader, sizeof(int), fieldName);
        var count = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        ValidateCount(count, maximum, fieldName);
        return count;
    }

    internal static void EnsureFullyConsumed<TReader>(TReader reader, string commandName)
        where TReader : notnull, IAsyncBinaryReader
    {
        if (reader.TryGetRemainingBytesCount(out var remaining) && remaining != 0L)
        {
            throw new SlimDataCommandFormatException(
                SlimDataCommandViolation.InvalidLength,
                $"{commandName} contains {remaining} trailing bytes.");
        }
    }

    internal static SlimDataCommandFormatException WrapStructuralException(
        string commandName,
        Exception exception)
        => exception is SlimDataCommandFormatException formatException
            ? formatException
            : new SlimDataCommandFormatException(
                exception is EndOfStreamException
                    ? SlimDataCommandViolation.Truncated
                    : SlimDataCommandViolation.InvalidFormat,
                $"Invalid or truncated {commandName} payload.",
                exception);

    internal static bool IsStructuralException(Exception exception)
        => exception is SlimDataCommandFormatException
            or EndOfStreamException
            or DecoderFallbackException
            or OverflowException
            or ArgumentException;

    private static void ValidateCommandSize<TReader>(TReader reader, string commandName)
        where TReader : notnull, IAsyncBinaryReader
    {
        if (!reader.TryGetRemainingBytesCount(out var remaining))
            return;

        if (remaining > MaxCommandBytes)
        {
            throw new SlimDataCommandFormatException(
                SlimDataCommandViolation.TooLarge,
                $"{commandName} payload is {remaining} bytes; maximum is {MaxCommandBytes} bytes.");
        }

        if (remaining < HeaderLength)
        {
            throw new SlimDataCommandFormatException(
                SlimDataCommandViolation.Truncated,
                $"{commandName} payload is shorter than the {HeaderLength}-byte envelope.");
        }
    }

    private static void ValidateLength(int length, int maximum, string fieldName)
    {
        if (length < 0)
        {
            throw new SlimDataCommandFormatException(
                SlimDataCommandViolation.InvalidLength,
                $"{fieldName} has negative length {length}.");
        }

        if (length > maximum)
        {
            throw new SlimDataCommandFormatException(
                SlimDataCommandViolation.TooLarge,
                $"{fieldName} is {length} bytes; maximum is {maximum} bytes.");
        }
    }

    private static void ValidateCount(int count, int maximum, string fieldName)
    {
        if (count < 0 || count > maximum)
        {
            throw new SlimDataCommandFormatException(
                SlimDataCommandViolation.InvalidCount,
                $"{fieldName} count {count} is outside the allowed range 0..{maximum}.");
        }
    }

    private static void EnsureRemaining<TReader>(TReader reader, long required, string fieldName)
        where TReader : notnull, IAsyncBinaryReader
    {
        if (reader.TryGetRemainingBytesCount(out var remaining) && remaining < required)
        {
            throw new SlimDataCommandFormatException(
                SlimDataCommandViolation.Truncated,
                $"{fieldName} requires {required} bytes but only {remaining} remain.");
        }
    }

    private static async ValueTask<int> ReadCompressedLengthAsync<TReader>(
        TReader reader,
        string fieldName,
        CancellationToken token)
        where TReader : notnull, IAsyncBinaryReader
    {
        uint result = 0U;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            EnsureRemaining(reader, sizeof(byte), fieldName);
            var current = await reader.ReadLittleEndianAsync<byte>(token).ConfigureAwait(false);
            if (shift == 28 && (current & 0xF8) != 0)
            {
                throw new SlimDataCommandFormatException(
                    SlimDataCommandViolation.InvalidLength,
                    $"{fieldName} has an overflowing compressed length prefix.");
            }

            result |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                if (shift > 0 && (current & 0x7F) == 0)
                {
                    throw new SlimDataCommandFormatException(
                        SlimDataCommandViolation.InvalidLength,
                        $"{fieldName} has a non-canonical compressed length prefix.");
                }

                if (result > int.MaxValue)
                {
                    throw new SlimDataCommandFormatException(
                        SlimDataCommandViolation.InvalidLength,
                        $"{fieldName} length exceeds Int32.MaxValue.");
                }

                return (int)result;
            }
        }

        throw new SlimDataCommandFormatException(
            SlimDataCommandViolation.InvalidLength,
            $"{fieldName} has an unterminated compressed length prefix.");
    }
}
