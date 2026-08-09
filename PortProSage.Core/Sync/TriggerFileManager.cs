using System.Text.Json;
using PortProSage.Core.Models;

namespace PortProSage.Core.Sync;

/// <summary>
/// Manages the simple file-drop protocol used for manual sync requests:
///   - PortProSage.Trigger writes {requestId}.request.json into the TriggerFolder.
///   - The Windows Service watches that folder, processes new files in arrival order,
///     then moves the request (and a {requestId}.result.json report) into
///     ProcessedTriggerFolder.
/// A simple file-drop was chosen over a local HTTP/named-pipe endpoint so the
/// manual-trigger tool has no dependency on the service's process being reachable
/// in any particular way - it just needs filesystem access to the same folder.
/// </summary>
public static class TriggerFileManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Write(string triggerFolder, SyncRequest request)
    {
        Directory.CreateDirectory(triggerFolder);
        var path = Path.Combine(triggerFolder, $"{request.RequestId}.request.json");
        File.WriteAllText(path, JsonSerializer.Serialize(request, JsonOptions));
        return path;
    }

    public static IEnumerable<(string Path, SyncRequest Request)> ReadPending(string triggerFolder)
    {
        if (!Directory.Exists(triggerFolder)) yield break;

        foreach (var file in Directory.EnumerateFiles(triggerFolder, "*.request.json").OrderBy(f => f))
        {
            SyncRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<SyncRequest>(File.ReadAllText(file));
            }
            catch
            {
                // malformed request file - leave it for manual inspection rather than looping on it forever
                continue;
            }

            if (request is not null)
            {
                yield return (file, request);
            }
        }
    }

    public static void Archive(string requestFilePath, string processedFolder, SyncResult result)
    {
        Directory.CreateDirectory(processedFolder);

        var fileName = Path.GetFileName(requestFilePath);
        var destRequest = Path.Combine(processedFolder, fileName);
        if (File.Exists(destRequest))
        {
            File.Delete(destRequest);
        }
        File.Move(requestFilePath, destRequest);

        var resultFileName = fileName.Replace(".request.json", ".result.json");
        var destResult = Path.Combine(processedFolder, resultFileName);
        File.WriteAllText(destResult, JsonSerializer.Serialize(result, JsonOptions));
    }

    /// <summary>Writes just the result half of a request/result pair whose request was
    /// already written via Write(folder, request) - no Archive/Move step, since the
    /// automatic poll (Worker.RunAutomaticLastChangedSyncAsync) builds and runs its
    /// own SyncRequest in one step rather than picking up a pre-existing pending file.
    /// Writing the request BEFORE calling RunAsync and the result only AFTER (i.e.
    /// only if the process didn't die mid-run) gives every automatic poll cycle the
    /// exact same on-disk lifecycle as a Manual Run: request-only-on-disk means
    /// interrupted, request+result means completed - so RunHistoryService can detect
    /// both the same way instead of needing separate log-reconstruction logic for
    /// the automatic case.</summary>
    public static void WriteResult(string folder, string requestId, SyncResult result)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{requestId}.result.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
    }
}
