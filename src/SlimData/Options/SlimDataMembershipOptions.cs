namespace SlimData.Options;

public sealed class SlimDataMembershipOptions
{
    public const string SectionName = "SlimData:Membership";

    public int ChangeTimeoutSeconds { get; set; } = 60;

    public int AnnouncementTimeoutSeconds { get; set; } = 70;
}
