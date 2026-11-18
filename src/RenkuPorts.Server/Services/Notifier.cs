using System.Net.Mail;
using Azure;
using Azure.Communication.Email;

namespace RenkuPorts.Server.Services;

/// <summary>Sends plain-text notification emails. Chosen by <c>Email:Mode</c> (<c>Smtp</c> or <c>AzureCommunication</c>).</summary>
public interface INotifier
{
    Task SendAsync(IReadOnlyCollection<string> to, string subject, string body);
}

/// <summary>SMTP delivery (locally: mailpit on port 1025, no auth, no TLS).</summary>
public class SmtpNotifier(IConfiguration config) : INotifier
{
    public async Task SendAsync(IReadOnlyCollection<string> to, string subject, string body)
    {
        if (to.Count == 0) return;
        using var smtp = new SmtpClient(config["Email:Smtp:Host"] ?? "localhost", config.GetValue("Email:Smtp:Port", 25));
        using var msg = new MailMessage { From = new MailAddress(From(config)), Subject = subject, Body = body };
        foreach (var t in to) msg.To.Add(t);
        await smtp.SendMailAsync(msg);
    }

    internal static string From(IConfiguration config) => config["Email:From"] ?? "noreply@renkuos.local";
}

/// <summary>Azure Communication Services Email delivery.</summary>
public class AzureEmailNotifier(IConfiguration config) : INotifier
{
    // Email:AzureCommunication:Endpoint (managed identity) wins over the connection string.
    private readonly EmailClient _client = config["Email:AzureCommunication:Endpoint"] is { Length: > 0 } endpoint
        ? new EmailClient(new Uri(endpoint), new Azure.Identity.DefaultAzureCredential())
        : new EmailClient(config["Email:AzureCommunication:ConnectionString"] is { Length: > 0 } cs ? cs
            : throw new InvalidOperationException("Set Email:AzureCommunication:Endpoint or Email:AzureCommunication:ConnectionString"));

    public async Task SendAsync(IReadOnlyCollection<string> to, string subject, string body)
    {
        if (to.Count == 0) return;
        var msg = new EmailMessage(SmtpNotifier.From(config), new EmailRecipients(to.Select(t => new EmailAddress(t))),
            new EmailContent(subject) { PlainText = body });
        await _client.SendAsync(WaitUntil.Started, msg);
    }
}
