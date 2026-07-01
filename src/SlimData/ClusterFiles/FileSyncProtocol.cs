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
    private const char TagPairSep = ';';
    private const char TagKvSep = '=';

    public const string AnnouncePrefix   = "slimfaas.file.announce";
    public const string TagsHeaderName    = "X-SlimFaas-Tags";

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

    public static string? BuildTagsHeaderValue(IDictionary<string, string>? tags)
    {
        if (tags is null || tags.Count == 0)
            return null;

        var parts = tags
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{Base64UrlCodec.Encode(kv.Key)}{TagKvSep}{Base64UrlCodec.Encode(kv.Value)}");

        return string.Join(TagPairSep, parts);
    }

    public static bool TryParseTagsHeaderValue(string? value, out IDictionary<string, string>? tags)
    {
        tags = null;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var pairs = value.Split(TagPairSep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var pair in pairs)
        {
            var idx = pair.IndexOf(TagKvSep);
            if (idx <= 0 || idx == pair.Length - 1)
                return false;

            var keyEncoded = pair[..idx];
            var valueEncoded = pair[(idx + 1)..];

            string key;
            string tagValue;
            try
            {
                key = Base64UrlCodec.Decode(keyEncoded);
                tagValue = Base64UrlCodec.Decode(valueEncoded);
            }
            catch
            {
                return false;
            }

            result[key] = tagValue;
        }

        tags = result;
        return true;
    }
}
