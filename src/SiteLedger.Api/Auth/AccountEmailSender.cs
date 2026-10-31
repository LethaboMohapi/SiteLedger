using System.Net;
using System.Net.Mail;

namespace SiteLedger.Api.Auth;

public interface IAccountEmailSender
{
    bool IsConfigured { get; }
    Task SendAsync(string address, string subject, string body, CancellationToken ct);
}

public sealed class SmtpAccountEmailSender(IConfiguration configuration) : IAccountEmailSender
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["Email:SmtpHost"]) && !string.IsNullOrWhiteSpace(configuration["Email:From"]);

    public async Task SendAsync(string address, string subject, string body, CancellationToken ct)
    {
        var host = configuration["Email:SmtpHost"];
        var sender = configuration["Email:From"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(sender)) throw new InvalidOperationException("SMTP is not configured.");
        var port = configuration.GetValue("Email:SmtpPort", 587);
        using var message = new MailMessage(sender, address, subject, body);
        using var client = new SmtpClient(host, port) { EnableSsl = true };
        var username = configuration["Email:Username"];
        if (!string.IsNullOrWhiteSpace(username)) client.Credentials = new NetworkCredential(username, configuration["Email:Password"]);
        await client.SendMailAsync(message, ct);
    }
}
