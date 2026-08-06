using System.Text.Json;
using PortProSage.Admin.Models;

namespace PortProSage.Admin.Services;

/// <summary>
/// Writes/reads the same file-drop protocol PortProSage.Trigger and the
/// Service's Worker already use (see PortProSage.Core.Sync.TriggerFileManager) -
/// duplicated here rather than referenced, to keep this project independent of
/// PortProSage.Core's net48/x86/Sage 50 SDK dependency chain.
/// </summary>
public static class TriggerService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string WriteRequest(string triggerFolder, SyncRequest request)
    {
        Directory.CreateDirectory(triggerFolder);
        var path = Path.Combine(triggerFolder, $"{request.RequestId}.request.json");
        File.WriteAllText(path, JsonSerializer.Serialize(request, JsonOptions));
        return path;
    }

    /// <summary>
    /// The Worker moves the request file (and writes a matching *.result.json)
    /// into ProcessedTriggerFolder once it's handled - null while still pending.
    /// </summary>
    public static SyncResult? TryReadResult(string processedTriggerFolder, string requestId)
    {
        var path = Path.Combine(processedTriggerFolder, $"{requestId}.result.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<SyncResult>(File.ReadAllText(path));
        }
        catch
        {
            return null; // file may still be mid-write; caller will just retry on the next poll
        }
    }
}
