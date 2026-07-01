namespace SlimFaas;

public static class DataFileKeys
{
    public const string MetaPrefix = "data:file:";
    public const string MetaSuffix = ":meta";
    public const string InternalElementIdPrefix = "sf-internal-";
    public const string InternalElementIdSuffix = "-internal";

    public static string MetaKey(string elementId) => $"{MetaPrefix}{elementId}{MetaSuffix}";

    public static string CreateInternalOffloadId() =>
        $"{InternalElementIdPrefix}{Guid.NewGuid():N}{InternalElementIdSuffix}";

    public static bool IsInternalElementId(string? elementId) =>
        !string.IsNullOrWhiteSpace(elementId) &&
        elementId.StartsWith(InternalElementIdPrefix, StringComparison.Ordinal) &&
        elementId.EndsWith(InternalElementIdSuffix, StringComparison.Ordinal) &&
        elementId.Length > InternalElementIdPrefix.Length + InternalElementIdSuffix.Length;
}
