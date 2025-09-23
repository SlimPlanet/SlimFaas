namespace GmailMailerApi.Models;

/// <summary>
/// SMTP configuration settings. For Gmail: Host=smtp.gmail.com, Port=587, UseStartTls=true.
/// Use a Google "App Password" (2FA enabled) for <see cref="Password"/>.
/// </summary>
public sealed class SmtpOptions
{
    /// <summary>SMTP server hostname (e.g., smtp.gmail.com).</summary>
    public string Host { get; init; } = "smtp.gmail.com";

    /// <summary>TCP port (e.g., 587 for STARTTLS).</summary>
    public int Port { get; init; } = 587;

    /// <summary>SMTP username (usually your full Gmail address).</summary>
    public string Username { get; init; } = default!;

    /// <summary>SMTP password (for Gmail: App Password, not your account password).</summary>
    public string Password { get; init; } = default!;

    /// <summary>Display name for the From header.</summary>
    public string? SenderDisplayName { get; init; }

    /// <summary>Whether to use STARTTLS (recommended for Gmail on port 587).</summary>
    public bool UseStartTls { get; init; } = true;
}
