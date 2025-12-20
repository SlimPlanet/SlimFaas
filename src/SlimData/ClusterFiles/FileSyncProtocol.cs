using System;

namespace SlimData.ClusterFiles;

public static class Base64UrlCodec
{
    public static string Encode(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var b64 = Convert.ToBase64String(bytes);
        return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static string Decode(string encoded)
    {
        var s = encoded.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        var bytes = Convert.FromBase64String(s);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}

public static class FileSyncProtocol
{
    private const char Sep = '|';

    public const string AnnouncePrefix   = "slimfaas.file.announce";
    public const string FetchPrefix      = "slimfaas.file.fetch";
    public const string FetchOkPrefix    = "slimfaas.file.fetch.ok";
    public const string FetchNotFound    = "slimfaas.file.fetch.notfound";

    // announce|idEnc|sha|len|contentTypeEnc|overwrite(0/1)
    public static string BuildAnnounceName(string idEncoded, string sha256Hex, long length, string contentType, bool overwrite)
        => $"{AnnouncePrefix}{Sep}{idEncoded}{Sep}{sha256Hex}{Sep}{length}{Sep}{Base64UrlCodec.Encode(contentType)}{Sep}{(overwrite ? 1 : 0)}";

    public static bool TryParseAnnounceName(string name, out string idEncoded, out string sha256Hex, out long length, out string contentType, out bool overwrite)
    {
        idEncoded = sha256Hex = contentType = "";
        length = 0;
        overwrite = false;

        if (!name.StartsWith(AnnouncePrefix + Sep, StringComparison.Ordinal))
            return false;

        var parts = name.Split(Sep);
        if (parts.Length != 6)
            return false;

        idEncoded = parts[1];
        sha256Hex = parts[2];

        if (!long.TryParse(parts[3], out length))
            return false;

        contentType = Base64UrlCodec.Decode(parts[4]);
        overwrite = parts[5] == "1";
        return true;
    }

    // fetch|idEnc|sha
    public static string BuildFetchName(string idEncoded, string sha256Hex)
        => $"{FetchPrefix}{Sep}{idEncoded}{Sep}{sha256Hex}";

    public static bool TryParseFetchName(string name, out string idEncoded, out string sha256Hex)
    {
        idEncoded = sha256Hex = "";
        if (!name.StartsWith(FetchPrefix + Sep, StringComparison.Ordinal))
            return false;

        var parts = name.Split(Sep);
        if (parts.Length != 3)
            return false;

        idEncoded = parts[1];
        sha256Hex = parts[2];
        return true;
    }

    // fetch.ok|idEnc|sha|len
    public static string BuildFetchOkName(string idEncoded, string sha256Hex, long length, long? expireAtUtcTicks = null)
        => expireAtUtcTicks is null  ? $"{FetchOkPrefix}{Sep}{idEncoded}{Sep}{sha256Hex}{Sep}{length}"
                                     : $"{FetchOkPrefix}{Sep}{idEncoded}{Sep}{sha256Hex}{Sep}{length}{Sep}{expireAtUtcTicks.Value}";

    public static bool TryParseFetchOkName(string name, out string idEncoded, out string sha256Hex, out long length, out long? expireAtUtcTicks)
    {
        idEncoded = sha256Hex = "";
        length = 0;
        expireAtUtcTicks = null;

        if (!name.StartsWith(FetchOkPrefix + Sep, StringComparison.Ordinal))
            return false;

        var parts = name.Split(Sep);
        if (parts.Length != 4 && parts.Length != 5)
            return false;

        idEncoded = parts[1];
        sha256Hex = parts[2];
        if (!long.TryParse(parts[3], out length))
            return false;
        
        if (parts.Length == 5 && long.TryParse(parts[4], out var exp) && exp > 0)
           expireAtUtcTicks = exp;
        
        return true;
    }
}
