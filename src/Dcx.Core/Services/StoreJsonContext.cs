using System.Text.Json.Serialization;

namespace DcxManager.Services;

/// <summary>Build-time JSON metadata for the store files every start reads (0.6.32, ADR-110): settings, data, the format
/// record and the workspace. Without it System.Text.Json reflected over a few hundred properties and emitted their
/// accessors as IL the first time each type was parsed — 60 ms on the UI thread inside the start-up check. The options
/// (<see cref="SecureStore.Json"/>) chain this context before the reflection resolver, so every other type keeps working
/// as before; the self-test checks that both produce the same JSON for the demo store.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppData))]
[JsonSerializable(typeof(StoreSchema.Meta))]
[JsonSerializable(typeof(List<Workspace.Entry>))]
internal sealed partial class StoreJsonContext : JsonSerializerContext
{
}
