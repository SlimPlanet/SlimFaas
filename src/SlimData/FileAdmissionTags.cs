using System.Globalization;

namespace SlimData;

public static class FileAdmissionTags
{
    public const string Token = "SlimFaasFileAdmissionToken";
    public const string Deadline = "SlimFaasFileAdmissionDeadlineUtcTicks";

    public static bool IsManaged(IDictionary<string, string>? tags) => tags?.ContainsKey(Token) == true;

    public static bool TryRead(IDictionary<string, string> tags, out string token, out long deadline)
    {
        token = "";
        deadline = 0;
        if (!tags.TryGetValue(Token, out string? value) || !Guid.TryParseExact(value, "N", out _) ||
            !tags.TryGetValue(Deadline, out string? expiry) ||
            !long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out deadline) ||
            deadline <= 0 || deadline > DateTime.MaxValue.Ticks)
            return false;
        token = value;
        return true;
    }
}
