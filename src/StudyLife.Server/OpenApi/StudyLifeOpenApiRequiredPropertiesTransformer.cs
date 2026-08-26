using System.Reflection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace StudyLife.Server.OpenApi;

/// <summary>
/// Audit finding D2 follow-up: the default build-time/runtime OpenAPI generation leaves
/// EVERY DTO component schema with an empty "required" array, because it derives required-ness
/// from C# member syntax (a required keyword / non-optional constructor parameter), not from
/// nullability - and every DTO in StudyLife.Shared/Dtos.cs is a plain mutable class with ordinary
/// (non-"required") auto-properties, whether or not the CLR type is itself nullable. That
/// under-specifies the contract for client generators: a caller has no way to tell from the spec
/// alone that e.g. StudySessionDto.Id/CourseId/CourseName/StartTime/EndTime/IsCompleted are
/// genuinely guaranteed on every response, while Topic/Notes/RecurrenceGroupId are not.
///
/// This transformer fixes that by deriving "required" from the underlying CLR property's
/// nullability instead: non-nullable value types (int, bool, DateTime, ...) and non-nullable
/// reference types (string, under this project's Nullable=enable) become required; Nullable&lt;T&gt;
/// (int?, DateTime?, decimal?, ...) and nullable reference types (string?) stay optional. The
/// schema's "type" nullability marking (e.g. "type": ["null","string"]) is untouched here - the
/// default generator already derives that correctly from the same JsonTypeInfo/nullability
/// metadata (spot-checked in the generated docs/api/openapi.json: nullable properties already
/// carry "null" in their "type" array, e.g. StudySessionDto.topic/notes/recurrenceGroupId,
/// UserSettingsDto.version/targetGraduationDate) - this transformer only adds the missing
/// "required" side of the contract.
///
/// NOTE on a wire-truth subtlety, worth calling out explicitly: Program.cs never configures
/// JsonSerializerOptions.DefaultIgnoreCondition, so System.Text.Json's default
/// (DefaultIgnoreCondition.Never) applies uniformly - EVERY property, nullable or not, is
/// ALWAYS serialized as a present JSON key (see NullableFieldPresenceTests in
/// tests/StudyLife.Server.Tests/ApiContractTests.cs, which pins exactly this). Strictly by that
/// wire truth, every key is "required" in the sense of "the key is always present". This
/// transformer deliberately follows the more common OpenAPI-consumer convention instead (CLR
/// nullable => optional in the schema, but still explicitly marked nullable) to match what the
/// generated client tooling in the consumer repos (studylife-mcp et al.) actually expects -
/// "required" here means "guaranteed non-null", not "guaranteed present", which the
/// nullable-and-not-required combination on the remaining fields still correctly documents as
/// "key always there, value may be null".
/// </summary>
public sealed class StudyLifeOpenApiRequiredPropertiesTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var jsonTypeInfo = context.JsonTypeInfo;
        if (jsonTypeInfo is null || schema.Properties is null || schema.Properties.Count == 0)
            return Task.CompletedTask;

        var nullabilityContext = new NullabilityInfoContext();

        foreach (var jsonProperty in jsonTypeInfo.Properties)
        {
            if (jsonProperty.AttributeProvider is not PropertyInfo propertyInfo) continue;
            if (!schema.Properties.ContainsKey(jsonProperty.Name)) continue;
            if (IsNullable(propertyInfo, nullabilityContext)) continue;

            schema.Required ??= new HashSet<string>();
            schema.Required.Add(jsonProperty.Name);
        }

        return Task.CompletedTask;
    }

    private static bool IsNullable(PropertyInfo property, NullabilityInfoContext nullabilityContext)
    {
        var propertyType = property.PropertyType;

        // Value types: only Nullable<T> (int?, DateTime?, decimal?, bool?, ...) is nullable -
        // every other value type (int, bool, DateTime, enum, ...) is unconditionally non-null.
        if (propertyType.IsValueType)
            return Nullable.GetUnderlyingType(propertyType) is not null;

        // Reference types: read the project's Nullable=enable annotations (string vs. string?,
        // List<T> vs. List<T>?, ...) via nullability metadata instead of assuming every
        // reference type is nullable - most DTO properties here are non-nullable reference types
        // with a "= ..." default (e.g. `public string CourseName { get; set; } = "";`).
        var info = nullabilityContext.Create(property);
        return info.ReadState == NullabilityState.Nullable || info.WriteState == NullabilityState.Nullable;
    }
}
