using System.Text.Json.Serialization;

namespace MeetVault.Views;

// Trim-safe JSON for the Gemini transcript payload (publish trims; reflection-based Serialize would break).
[JsonSerializable(typeof(List<TranscriptLine>))]
internal sealed partial class TranscriptJsonContext : JsonSerializerContext
{
}

