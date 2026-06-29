namespace SlimFaas;

public static class DataFileKeys
{
    public const string MetaPrefix = "data:file:";
    public const string MetaSuffix = ":meta";

    public static string MetaKey(string elementId) => $"{MetaPrefix}{elementId}{MetaSuffix}";
}
