namespace SlimData.Options;

public sealed class SlimDataMembershipOptions
{
    public const string SectionName = "SlimData:Membership";

    public int ChangeTimeoutSeconds { get; set; } = 180;

    public int AnnouncementTimeoutSeconds { get; set; } = 200;

    public int RemovalMissingCycles { get; set; } = 3;
}
