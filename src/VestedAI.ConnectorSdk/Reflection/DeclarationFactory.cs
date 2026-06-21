using System.Text.Json.Nodes;
using NJsonSchema;
using NJsonSchema.Generation;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Tool;

namespace VestedAI.ConnectorSdk.Reflection;

/// <summary>
/// Converts annotated .NET types into normalized declaration objects.
/// Called by the assembly scanner (K-4) and exposed here so tests can
/// exercise declaration building in isolation.
/// </summary>
public static class DeclarationFactory
{
    /// <summary>
    /// Schema-generation settings shared by tool input/output schema building.
    /// </summary>
    /// <remarks>
    /// <c>FlattenInheritanceHierarchy</c> is essential: by default NJsonSchema
    /// represents class inheritance as
    /// <c>allOf: [{$ref: base}, {derived props, additionalProperties:false}]</c>
    /// with the base definition <em>also</em> <c>additionalProperties:false</c>.
    /// That composition is unsatisfiable — JSON Schema evaluates
    /// <c>additionalProperties</c> per-subschema, so the derived branch rejects
    /// every base-level property and the base branch rejects every derived
    /// property; only <c>{}</c> validates. A connector whose args type subclasses
    /// a shared base would then have every tool call rejected by the hub with
    /// "additional properties ... not allowed". Flattening collapses the
    /// hierarchy into a single object schema where inherited and derived
    /// properties share one <c>additionalProperties:false</c>.
    /// </remarks>
    private static readonly SystemTextJsonSchemaGeneratorSettings SchemaSettings = new()
    {
        FlattenInheritanceHierarchy = true,
    };


    /// <summary>
    /// Build an <see cref="AgentDeclaration"/> from a class decorated with
    /// <see cref="AgentAttribute"/> and zero-or-more <see cref="InstructionAttribute"/>s.
    /// </summary>
    /// <exception cref="ConnectorException">Thrown when <c>[Agent]</c> is missing.</exception>
    public static AgentDeclaration FromAgentType(Type t)
    {
        var agentAttr = t.GetCustomAttributes(typeof(AgentAttribute), inherit: false)
                         .Cast<AgentAttribute>()
                         .FirstOrDefault()
                    ?? throw new ConnectorException(
                           $"Type {t.FullName} is missing the [Agent] attribute.");

        var instructions = t.GetCustomAttributes(typeof(InstructionAttribute), inherit: false)
                            .Cast<InstructionAttribute>()
                            .OrderBy(i => i.Position)
                            .Select(i => new InstructionDeclaration(i.Type, i.Position, i.Body, i.Format))
                            .ToList();

        return new AgentDeclaration(
            Key:          agentAttr.Key,
            Name:         agentAttr.Name,
            Model:        agentAttr.Model,
            Description:  agentAttr.Description,
            Status:       agentAttr.Status,
            Instructions: instructions);
    }

    /// <summary>
    /// Build a <see cref="ToolDeclaration"/> from a class decorated with
    /// <see cref="ToolAttribute"/> that also subclasses
    /// <c>ToolHandler&lt;TArgs, TResult&gt;</c>.
    /// </summary>
    /// <remarks>
    /// NJsonSchema generates the input/output schemas synchronously via
    /// <c>JsonSchema.FromType(type).ToJson()</c>.
    /// <c>[System.ComponentModel.Description]</c> on properties flows
    /// into the schema's <c>properties.&lt;prop&gt;.description</c> field.
    /// </remarks>
    /// <exception cref="ConnectorException">
    /// Thrown when <c>[Tool]</c> is missing, <paramref name="t"/> does not
    /// subclass <c>ToolHandler&lt;,&gt;</c>, or the declared sensitivity is
    /// not in <see cref="Sensitivity.All"/>.
    /// </exception>
    public static ToolDeclaration FromToolType(Type t)
    {
        var toolAttr = t.GetCustomAttributes(typeof(ToolAttribute), inherit: false)
                        .Cast<ToolAttribute>()
                        .FirstOrDefault()
                   ?? throw new ConnectorException(
                          $"Type {t.FullName} is missing the [Tool] attribute.");

        // Walk base-type chain to find ToolHandler<TArgs, TResult>.
        (Type argsType, Type resultType) = ResolveHandlerGenericArgs(t)
            ?? throw new ConnectorException(
                   $"Type {t.FullName} must subclass ToolHandler<TArgs, TResult>.");

        // Validate sensitivity.
        var sensitivity = toolAttr.Sensitivity ?? "";
        if (sensitivity != "" && !Sensitivity.All.Contains(sensitivity))
        {
            throw new ConnectorException(
                $"[Tool(\"{toolAttr.Key}\")] Sensitivity \"{sensitivity}\" is not valid. " +
                $"Allowed values: {string.Join(", ", Sensitivity.All)}.");
        }

        // Generate JSON schemas synchronously, then normalize the $schema
        // dialect to draft-07.
        var inputSchemaJson  = NormalizeSchemaDialect(JsonSchema.FromType(argsType, SchemaSettings).ToJson());
        var outputSchemaJson = NormalizeSchemaDialect(JsonSchema.FromType(resultType, SchemaSettings).ToJson());

        return new ToolDeclaration
        {
            Key             = toolAttr.Key,
            Name            = string.IsNullOrEmpty(toolAttr.Name) ? toolAttr.Key : toolAttr.Name,
            Description     = toolAttr.Description,
            Sensitivity     = sensitivity,
            DefaultDeadlineMs = toolAttr.DefaultDeadlineMs,
            MaxResultBytes  = toolAttr.MaxResultBytes,
            InputSchemaJson = inputSchemaJson,
            OutputSchemaJson = outputSchemaJson,
            HandlerType     = t,
            ArgsType        = argsType,
            ResultType      = resultType,
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// NJsonSchema emits <c>"$schema": "http://json-schema.org/draft-04/schema#"</c>.
    /// The hub validates declared tool schemas with opis/json-schema, which
    /// supports drafts 06/07/2019-09/2020-12 but NOT draft-04 — so a draft-04
    /// document is rejected with a <c>schema_invalid</c> registration issue.
    /// Rewrite the dialect to draft-07 (what the Node SDK's zod output uses,
    /// proven compatible) so the document compiles. Keywords NJsonSchema emits
    /// (<c>type</c>, <c>properties</c>, <c>definitions</c>, <c>$ref</c>,
    /// <c>required</c>, <c>description</c>) are all valid under draft-07.
    /// </summary>
    private static string NormalizeSchemaDialect(string schemaJson)
    {
        var node = JsonNode.Parse(schemaJson);
        if (node is JsonObject obj)
        {
            obj["$schema"] = "http://json-schema.org/draft-07/schema#";
            return obj.ToJsonString();
        }
        return schemaJson;
    }

    /// <summary>
    /// Walk the base-type chain of <paramref name="t"/> looking for a closed
    /// generic <c>ToolHandler&lt;TArgs, TResult&gt;</c> base type and extract
    /// the two type arguments.
    /// </summary>
    private static (Type argsType, Type resultType)? ResolveHandlerGenericArgs(Type t)
    {
        var handlerOpen = typeof(ToolHandler<,>);
        var current = t.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == handlerOpen)
            {
                var args = current.GetGenericArguments();
                return (args[0], args[1]);
            }
            current = current.BaseType;
        }
        return null;
    }
}
