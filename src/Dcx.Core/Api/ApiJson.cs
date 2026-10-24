using System.Text.Json;
using System.Text.Json.Serialization;

namespace DcxManager.Api;

/// <summary>The JSON shape of everything the app answers to programs: the local API and the add-on bridge (camelCase, no
/// nulls, enums as camelCase strings). In Core since 0.2.0 phase 6, so the add-ons below the app share it.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
