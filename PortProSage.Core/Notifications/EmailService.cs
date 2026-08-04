using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Models;

namespace PortProSage.Core.Notifications;

/// <summary>
/// Emails the failed-transactions CSV (see FailedTransactionReport) after a sync
/// run that had at least one failure. Disabled (Email:Enabled=false) and blank
/// by default, same placeholder pattern as PortPro/Sage50 secrets - fill in real
/// SMTP settings via user-secrets/environment variables, not the committed
/// appsettings.json.
/// </summary>
public class EmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<EmailService> _logger;

    public EmailService(EmailSettings settings, ILogger<EmailService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task SendFailedTransactionsAsync(string csvFilePath, SyncResult result, CancellationToken ct)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Email notifications disabled (Email:Enabled=false) - not sending {Path}", csvFilePath);
            return;
        }

        var recipients = _settings.RecipientAddressesCsv
            .Split(',')
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .ToList();

        if (recipients.Count == 0)
        {
            _logger.LogWarning("Email:RecipientAddressesCsv is empty - cannot send failed-transaction notification for {Path}", csvFilePath);
            return;
        }

        var failedCount = result.InvoicesFailedValidation + result.InvoicesFailedImport;

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_settings.FromAddress),
                Subject = $"PortProSageSync: {failedCount} failed transaction(s) - {result.RequestId}",
                Body = $"Sync {result.RequestId} finished {result.FinishedAtUtc:u}.\n" +
                       $"Fetched: {result.InvoicesFetched}, Imported: {result.InvoicesImported}, " +
                       $"Skipped (already imported): {result.InvoicesSkippedAlreadyImported}, " +
                       $"Failed validation: {result.InvoicesFailedValidation}, Failed import: {result.InvoicesFailedImport}.\n\n" +
                       "See the attached CSV for per-invoice details."
            };

            foreach (var recipient in recipients)
            {
                message.To.Add(recipient);
            }

            using var attachment = new Attachment(csvFilePath);
            message.Attachments.Add(attachment);

            using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
            {
                EnableSsl = _settings.UseSsl,
                Credentials = new NetworkCredential(_settings.Username, _settings.Password)
            };

            await client.SendMailAsync(message);
            _logger.LogInformation(
                "Sent failed-transaction notification ({Count} failure(s), {Path}) to {Recipients}",
                failedCount, csvFilePath, string.Join(", ", recipients));
        }
        catch (Exception ex)
        {
            // Never let a notification failure take down the sync itself - the CSV
            // is already written to disk regardless, so nothing is lost.
            _logger.LogError(ex, "Failed to send failed-transaction notification email for {Path}", csvFilePath);
        }
    }
}
