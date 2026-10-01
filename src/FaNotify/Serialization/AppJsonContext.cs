using System.Text.Json;
using System.Text.Json.Serialization;
using FaNotify.Configuration;
using FaNotify.Discord;
using FaNotify.Solver;
using FaNotify.State;

namespace FaNotify.Serialization;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(NotificationState))]
[JsonSerializable(typeof(DiscordPayload))]
[JsonSerializable(typeof(SolverRequest))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
