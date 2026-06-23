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

        // Walk base-type chain to find ToolHandler<TArgs, TResult> or PaginatedToolHandler<TArgs, TRow>.
        (Type argsType, Type resultType, bool paginated) = ResolveHandlerGenericArgs(t)
            ?? throw new ConnectorException(
                   $"Type {t.FullName} must subclass ToolHandler<,> or PaginatedToolHandler<,>.");

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
            IsPaginated     = paginated,
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Normalize raw NJsonSchema output into a document the hub will accept.
    /// Two repairs:
    /// <list type="number">
    /// <item><b>Dialect.</b> NJsonSchema emits
    /// <c>"$schema": "http://json-schema.org/draft-04/schema#"</c>. The hub
    /// validates declared schemas with opis/json-schema (drafts 06/07/2019-09/
    /// 2020-12, NOT draft-04), so a draft-04 document is rejected with a
    /// <c>schema_invalid</c> registration issue. Rewrite the dialect to draft-07.</item>
    /// <item><b>"Allow any" <c>additionalProperties</c>.</b> The "any" schema of
    /// an object-valued dictionary (<c>Dictionary&lt;string, object&gt;</c>) is
    /// emitted by NJsonSchema as <c>"additionalProperties": {}</c> (and, on some
    /// 11.x versions, the already-invalid <c>"additionalProperties": []</c>).
    /// Both forms are normalized to the boolean <c>"additionalProperties": true</c>,
    /// which is semantically identical ("allow any additional property"). This is
    /// not cosmetic: the empty-object form <c>{}</c> is silently corrupted to the
    /// invalid array form <c>[]</c> by any hop that round-trips the schema through
    /// a PHP associative decode (<c>json_decode($s, true)</c> cannot distinguish
    /// <c>{}</c> from <c>[]</c>) — e.g. the ConnectorHub's Laravel baseline store.
    /// The result then fails draft-07 metaschema validation at tool-call time
    /// ("output schema compile: ... additionalProperties: got array, want boolean
    /// or object"). The boolean <c>true</c> is a scalar that survives that
    /// round-trip intact. The SDK does not trust the floating <c>NJsonSchema 11.*</c>
    /// dependency, nor downstream JSON handling, to preserve the empty-object form.</item>
    /// </list>
    /// </summary>
    internal static string NormalizeSchemaDialect(string schemaJson)
    {
        var node = JsonNode.Parse(schemaJson);
        if (node is JsonObject obj)
        {
            obj["$schema"] = "http://json-schema.org/draft-07/schema#";
            RepairInvalidSchemaNodes(obj);
            return obj.ToJsonString();
        }
        return schemaJson;
    }

    /// <summary>
    /// Recursively normalize the "allow any" <c>additionalProperties</c> schema to
    /// the boolean form <c>true</c>. NJsonSchema emits it as an empty object
    /// <c>{}</c> (or, on some 11.x versions, the invalid empty array <c>[]</c>);
    /// both mean "any additional property is allowed". The empty-object form is
    /// fragile — it is mangled to the invalid <c>[]</c> by any PHP associative
    /// round-trip downstream (see <see cref="NormalizeSchemaDialect"/>) — so we
    /// rewrite both the array and empty-object forms to the scalar <c>true</c>,
    /// which survives such round-trips and is valid against the metaschema. A
    /// non-empty <c>additionalProperties</c> schema, and the boolean <c>false</c>
    /// (meaning "no additional properties"), are left untouched.
    /// </summary>
    private static void RepairInvalidSchemaNodes(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.TryGetPropertyValue("additionalProperties", out var ap)
                    && (ap is JsonArray || (ap is JsonObject apObj && apObj.Count == 0)))
                {
                    obj["additionalProperties"] = JsonValue.Create(true);
                }
                // Snapshot values before recursing — nested repairs mutate child
                // objects, not the collection being enumerated here.
                foreach (var child in obj.Select(kv => kv.Value).ToList())
                {
                    RepairInvalidSchemaNodes(child);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr.ToList())
                {
                    RepairInvalidSchemaNodes(item);
                }
                break;
        }
    }

    /// <summary>
    /// Walk the base-type chain of <paramref name="t"/> looking for a closed
    /// generic <c>ToolHandler&lt;TArgs, TResult&gt;</c> or
    /// <c>PaginatedToolHandler&lt;TArgs, TRow&gt;</c> base type and extract
    /// the two type arguments together with a flag indicating which it found.
    /// </summary>
    private static (Type argsType, Type resultType, bool paginated)? ResolveHandlerGenericArgs(Type t)
    {
        var single = typeof(ToolHandler<,>);
        var paged  = typeof(PaginatedToolHandler<,>);
        for (var cur = t.BaseType; cur is not null; cur = cur.BaseType)
        {
            if (cur.IsGenericType)
            {
                var def = cur.GetGenericTypeDefinition();
                if (def == single) { var a = cur.GetGenericArguments(); return (a[0], a[1], false); }
                if (def == paged)  { var a = cur.GetGenericArguments(); return (a[0], a[1], true); }
            }
        }
        return null;
    }
}
