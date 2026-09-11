using GmailMailerApi.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GmailMailerApi.Services;

/// <summary>
/// Sends emails using SMTP (MailKit). For Gmail, use App Password + STARTTLS on 587.
/// </summary>
public sealed class EmailService(SmtpOptions options, ILogger<EmailService> logger)
{
    private readonly SmtpOptions _opt = options;
    private readonly ILogger<EmailService> _log = logger;

    /// <summary>Sends an email with optional attachments.</summary>
    public async Task SendAsync(EmailRequestBase req, IFormFileCollection? attachments = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.Username) || string.IsNullOrWhiteSpace(_opt.Password))
            throw new InvalidOperationException("SMTP credentials are not configured.");

        if (string.IsNullOrWhiteSpace(req.Text) && string.IsNullOrWhiteSpace(req.Html))
            throw new ArgumentException("Provide at least 'Text' or 'Html' body.");

        var message = new MimeMessage();

        // From
        var fromName = string.IsNullOrWhiteSpace(_opt.SenderDisplayName) ? _opt.Username : _opt.SenderDisplayName;
        message.From.Add(new MailboxAddress(fromName, _opt.Username));

        // To/Cc/Bcc
        foreach (var to in req.To) message.To.Add(MailboxAddress.Parse(to));
        if (req.Cc is { Count: > 0 })
            foreach (var cc in req.Cc) message.Cc.Add(MailboxAddress.Parse(cc));
        if (req.Bcc is { Count: > 0 })
            foreach (var bcc in req.Bcc) message.Bcc.Add(MailboxAddress.Parse(bcc));

        message.Subject = req.Subject;

        if (!string.IsNullOrWhiteSpace(req.ReplyTo))
            message.ReplyTo.Add(MailboxAddress.Parse(req.ReplyTo));

        var bodyBuilder = new BodyBuilder();

        if (!string.IsNullOrEmpty(req.Text)) bodyBuilder.TextBody = req.Text;
        if (!string.IsNullOrEmpty(req.Html)) bodyBuilder.HtmlBody = req.Html;

        if (attachments is { Count: > 0 })
        {
            foreach (var file in attachments)
            {
                if (file.Length <= 0) continue;
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                ms.Position = 0;
                bodyBuilder.Attachments.Add(
                    file.FileName,
                    ms.ToArray(),
                    ContentType.Parse(file.ContentType ?? "application/octet-stream"));
            }
        }

        message.Body = bodyBuilder.ToMessageBody();

        using var smtp = new SmtpClient();
        try
        {
            await smtp.ConnectAsync(_opt.Host, _opt.Port, _opt.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
            await smtp.AuthenticateAsync(_opt.Username, _opt.Password, ct);
            await smtp.SendAsync(message, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed sending email via SMTP: {Message}", ex.Message);
            throw;
        }
        finally
        {
            try { await smtp.DisconnectAsync(true, ct); } catch { /* ignore */ }
        }
    }
}
