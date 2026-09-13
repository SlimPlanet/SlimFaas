using Microsoft.AspNetCore.Http;

namespace GmailMailerApi.Models;

/// <summary>
/// Form-data email request with attachments.
/// </summary>
public sealed class EmailRequestForm : EmailRequestBase
{
    /// <summary>Attachments uploaded as files in multipart/form-data.</summary>
    public IFormFileCollection? Attachments { get; init; }
}
