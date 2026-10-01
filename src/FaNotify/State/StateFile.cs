using System.Text.Json;
using FaNotify.Serialization;

namespace FaNotify.State;

internal static class StateFile
{
    public static NotificationState Load(string path)
    {
        if (!File.Exists(path))
        {
            return new NotificationState();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, AppJsonContext.Default.NotificationState)
            ?? throw new InvalidDataException("Notification state file contains invalid JSON.");
    }

    public static void Save(string path, NotificationState state)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, AppJsonContext.Default.NotificationState));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
