using System.ComponentModel.DataAnnotations;

namespace GmailMailerApi.Models;

/// <summary>
/// Base email request contract shared by JSON and Form endpoints.
/// </summary>
public class EmailRequestBase
{
    /// <summary>Primary recipients.</summary>
    [Required, MinLength(1)]
    public List<string> To { get; init; } = [];

    /// <summary>Carbon copy recipients.</summary>
    public List<string>? Cc { get; init; }

    /// <summary>Blind carbon copy recipients.</summary>
    public List<string>? Bcc { get; init; }

    /// <summary>Email subject.</summary>
    [Required]
    public string Subject { get; init; } = default!;

    /// <summary>Plain text body (optional if HTML is provided).</summary>
    public string? Text { get; init; }

    /// <summary>HTML body (optional if Text is provided).</summary>
    public string? Html { get; init; }

    /// <summary>Reply-To email address.</summary>
    [EmailAddress]
    public string? ReplyTo { get; init; }
}
