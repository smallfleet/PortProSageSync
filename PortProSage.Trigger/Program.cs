using PortProSage.Core.Models;
using PortProSage.Core.Sync;

// Usage examples:
//   PortProSage.Trigger.exe --folder "C:\PortProSageSync\requests" --mode lastchanged --from 2026-07-01 --to 2026-08-01
//   PortProSage.Trigger.exe --folder "C:\PortProSageSync\requests" --mode invoicerange --start INV-1000 --end INV-1050
//   PortProSage.Trigger.exe --folder "C:\PortProSageSync\requests" --mode completedate --from 2026-07-15 --to 2026-07-31
//
// This tool only writes a request file for the running PortProSage.Service to pick up
// (it polls its trigger folder roughly every 15 seconds) - it does not talk to PortPro
// or Sage 50 directly. Check <folder>\processed\<requestId>.result.json afterwards for
// the outcome.

var args_ = ParseArgs(args);

if (!args_.TryGetValue("folder", out var folder))
{
    Console.Error.WriteLine("Missing required --folder <path-to-service-trigger-folder> argument.");
    Console.Error.WriteLine("(This must match the Sync:TriggerFolder value in the service's appsettings.json.)");
    return 1;
}

if (!args_.TryGetValue("mode", out var mode))
{
    Console.Error.WriteLine("Missing required --mode lastchanged|invoicerange|completedate argument.");
    return 1;
}

var request = new SyncRequest();

switch (mode.ToLowerInvariant())
{
    case "lastchanged":
        request.FilterType = FilterType.LastChangedDate;
        request.From = ParseDate(args_, "from");
        request.To = ParseDate(args_, "to");
        break;

    case "invoicerange":
        request.FilterType = FilterType.InvoiceNumberRange;
        args_.TryGetValue("start", out var start);
        args_.TryGetValue("end", out var end);
        request.StartInvoiceNumber = start;
        request.EndInvoiceNumber = end;
        break;

    case "completedate":
        request.FilterType = FilterType.CompletedDateRange;
        request.From = ParseDate(args_, "from");
        request.To = ParseDate(args_, "to");
        break;

    default:
        Console.Error.WriteLine($"Unknown --mode '{mode}'. Expected lastchanged, invoicerange, or completedate.");
        return 1;
}

var path = TriggerFileManager.Write(folder, request);
Console.WriteLine($"Wrote sync request {request.RequestId} to {path}");
Console.WriteLine($"The service polls its trigger folder about every 15 seconds - check " +
                   $"{Path.Combine(folder, "processed", $"{request.RequestId}.result.json")} shortly for the outcome.");
return 0;

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].StartsWith("--"))
        {
            result[args[i].Substring(2)] = args[i + 1];
        }
    }
    return result;
}

static DateTimeOffset? ParseDate(Dictionary<string, string> args, string key)
{
    if (!args.TryGetValue(key, out var value)) return null;
    return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}
