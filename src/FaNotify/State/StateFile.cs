using System.Text.Json;

namespace FaNotify.State;

internal static class StateFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static NotificationState Load(string path)
    {
        if (!File.Exists(path))
        {
            return new NotificationState();
        }

        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("counts", out _))
        {
            return JsonSerializer.Deserialize<NotificationState>(json, JsonOptions)
                ?? throw new InvalidDataException("Notification state file contains invalid JSON.");
        }

        var legacyCounts = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions)
            ?? throw new InvalidDataException("Notification state file contains invalid JSON.");
        return new NotificationState { Counts = legacyCounts };
    }

    public static void Save(string path, NotificationState state)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
