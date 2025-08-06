using System.IO.Hashing;

public static class IdGenerator
{
    /// <summary>
    /// Calcule un identifiant 32 bits non-cryptographique (xxHash).
    /// </summary>
    public static uint GetId32(ReadOnlySpan<byte> data)
        // Variante 64 bits : XxHash64.HashToUInt64(data)
        => XxHash32.HashToUInt32(data);

    /// <summary>
    /// Si vous préférez un texte en hexadécimal (toujours 8 caractères).
    /// </summary>
    public static string GetId32Hex(ReadOnlySpan<byte> data)
        => GetId32(data).ToString("x8");
}
