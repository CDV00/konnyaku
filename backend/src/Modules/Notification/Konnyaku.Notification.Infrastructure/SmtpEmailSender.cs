using System.Net.Sockets;
using Konnyaku.Notification.Application;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Konnyaku.Notification.Infrastructure;

/// <summary>
/// Sends through any SMTP server (Mailpit in dev, a transactional provider in production). One connection per message:
/// volumes are small and it keeps the sender stateless. 5xx replies are permanent, everything else is retried.
/// </summary>
public sealed class SmtpEmailSender(SmtpOptions options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task<DeliveryResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Host) || string.IsNullOrWhiteSpace(options.FromAddress))
        {
            return DeliveryResult.Transient("smtp_not_configured");
        }

        MimeMessage mime;
        try
        {
            mime = Build(message);
        }
        catch (ParseException ex)
        {
            return DeliveryResult.Permanent($"invalid_address: {ex.Message}");
        }

        using var client = new SmtpClient { Timeout = options.TimeoutSeconds * 1000 };
        try
        {
            await client.ConnectAsync(options.Host, options.Port, Security(), cancellationToken);
            if (!string.IsNullOrEmpty(options.Username))
            {
                await client.AuthenticateAsync(options.Username, options.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return DeliveryResult.Ok;
        }
        catch (SmtpCommandException ex) when ((int)ex.StatusCode >= 500 && ex.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.MessageNotAccepted)
        {
            return DeliveryResult.Permanent($"smtp_{(int)ex.StatusCode}: {ex.Message}");
        }
        catch (SmtpCommandException ex)
        {
            return DeliveryResult.Transient($"smtp_{(int)ex.StatusCode}: {ex.Message}");
        }
        catch (Exception ex) when (ex is SmtpProtocolException or AuthenticationException or SocketException or IOException
                                       or ServiceNotConnectedException or SslHandshakeException or TimeoutException)
        {
            logger.LogWarning(ex, "SMTP delivery to {Host}:{Port} failed", options.Host, options.Port);
            return DeliveryResult.Transient($"smtp_unavailable: {ex.Message}");
        }
    }

    private MimeMessage Build(EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(message.FromName) ? options.FromName : message.FromName,
            options.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName ?? string.Empty, MailboxAddress.Parse(message.To).Address));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.Html, TextBody = message.Text }.ToMessageBody();
        return mime;
    }

    private SecureSocketOptions Security() => options.Security.ToLowerInvariant() switch
    {
        "none" => SecureSocketOptions.None,
        "starttls" => SecureSocketOptions.StartTls,
        "sslonconnect" => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto,
    };
}
