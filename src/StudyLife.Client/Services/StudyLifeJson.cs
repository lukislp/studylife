using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

/// <summary>
/// The JSON options for talking to the app's own API: the source-generated
/// <see cref="StudyLifeJsonContext"/> first, reflection as the fallback for any type not listed
/// there, so call sites can adopt these options wholesale without every DTO having to be
/// registered up front. Same wire format as before (JsonSerializerDefaults.Web).
/// </summary>
public static class StudyLifeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolverChain = { StudyLifeJsonContext.Default, new DefaultJsonTypeInfoResolver() },
    };
}
