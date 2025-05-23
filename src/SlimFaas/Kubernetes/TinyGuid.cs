using System.Security.Cryptography;

namespace SlimFaas.Kubernetes;

public static class TinyGuid
{
    private const string Alphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"; // base 62

    /// <summary>
    /// Génère un identifiant court et (statistiquement) unique.
    /// </summary>
    /// <param name="length">Taille voulue, 8 par défaut.</param>
    public static string NewTinyGuid(int length = 8)
    {
        // 62^5 ≈ 30 bits → 4 octets suffisent
        Span<byte> buffer = stackalloc byte[4];
        RandomNumberGenerator.Fill(buffer);

        uint value = BitConverter.ToUInt32(buffer);

        // Encodage base 62, LSB en premier pour éviter une division 64 bits
        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = Alphabet[(int)(value % (uint)Alphabet.Length)];
            value   /= (uint)Alphabet.Length;
        }
        return new string(chars);
    }
}
