using System.Text.Json.Nodes;
using NJsonSchema;
using NJsonSchema.Generation;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Credential;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Schema;
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
    /// Build a <see cref="CredentialDeclaration"/> from a class decorated with
    /// <see cref="CredentialAttribute"/> and one
    /// <see cref="CredentialFieldAttribute"/> per form field.
    /// </summary>
    /// <remarks>
    /// Validation is strict and happens at startup rather than at registration:
    /// a malformed credential schema would otherwise surface as a rejected
    /// <c>Register</c> or, worse, a form the user cannot complete.
    /// </remarks>
    /// <exception cref="ConnectorException">
    /// Thrown when <c>[Credential]</c> is missing, the type does not implement
    /// <see cref="IUserCredentialHandler"/>, the kind or a field type is not
    /// canonical, no fields are declared, a field key is blank or duplicated,
    /// or a "select" field declares no options.
    /// </exception>
    public static CredentialDeclaration FromCredentialType(Type t)
    {
        var credAttr = t.GetCustomAttributes(typeof(CredentialAttribute), inherit: false)
                        .Cast<CredentialAttribute>()
                        .FirstOrDefault()
                   ?? throw new ConnectorException(
                          $"Type {t.FullName} is missing the [Credential] attribute.");

        if (!typeof(IUserCredentialHandler).IsAssignableFrom(t))
        {
            throw new ConnectorException(
                $"Type {t.FullName} is decorated with [Credential] but does not implement " +
                "IUserCredentialHandler.");
        }

        // The parameterless-constructor requirement is enforced in Build(), and
        // only when the SDK has to construct the handler itself — a handler with
        // dependencies can be supplied ready-made via UseCredentialHandler.
        if (t.IsAbstract)
        {
            throw new ConnectorException(
                $"Credential handler {t.FullName} must be a concrete class.");
        }

        var kind = string.IsNullOrWhiteSpace(credAttr.Kind) ? "basic" : credAttr.Kind;
        if (!CredentialKinds.All.Contains(kind, StringComparer.Ordinal))
        {
            throw new ConnectorException(
                $"Credential handler {t.FullName} declares kind \"{kind}\"; " +
                $"expected one of: {string.Join(", ", CredentialKinds.All)}.");
        }

        if (string.IsNullOrWhiteSpace(credAttr.Title))
        {
            throw new ConnectorException(
                $"Credential handler {t.FullName} must declare a Title — it is the heading " +
                "of the form the user fills in.");
        }

        var fieldAttrs = t.GetCustomAttributes(typeof(CredentialFieldAttribute), inherit: false)
                          .Cast<CredentialFieldAttribute>()
                          .ToList();

        if (fieldAttrs.Count == 0)
        {
            throw new ConnectorException(
                $"Credential handler {t.FullName} declares no [CredentialField]s; the platform " +
                "would render an empty form.");
        }

        var fields = new List<CredentialFieldDeclaration>(fieldAttrs.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var f in fieldAttrs)
        {
            if (string.IsNullOrWhiteSpace(f.Key))
            {
                throw new ConnectorException(
                    $"Credential handler {t.FullName} declares a [CredentialField] with no Key.");
            }

            if (!seenKeys.Add(f.Key))
            {
                throw new ConnectorException(
                    $"Credential handler {t.FullName} declares duplicate field key \"{f.Key}\".");
            }

            var type = string.IsNullOrWhiteSpace(f.Type) ? "text" : f.Type;
            if (!CredentialFieldTypes.All.Contains(type, StringComparer.Ordinal))
            {
                throw new ConnectorException(
                    $"Credential field \"{f.Key}\" on {t.FullName} declares type \"{type}\"; " +
                    $"expected one of: {string.Join(", ", CredentialFieldTypes.All)}.");
            }

            var options = f.Options ?? Array.Empty<string>();
            if (type == "select" && options.Length == 0)
            {
                throw new ConnectorException(
                    $"Credential field \"{f.Key}\" on {t.FullName} is a \"select\" but declares " +
                    "no Options.");
            }

            fields.Add(new CredentialFieldDeclaration(
                Key:         f.Key,
                Label:       string.IsNullOrWhiteSpace(f.Label) ? f.Key : f.Label,
                Type:        type,
                Required:    f.Required,
                Placeholder: f.Placeholder ?? "",
                Options:     options));
        }

        return new CredentialDeclaration(
            Kind:        kind,
            Title:       credAttr.Title,
            HelpText:    credAttr.HelpText ?? "",
            Fields:      fields,
            HandlerType: t);
    }

    /// <summary>
    /// Build a <see cref="RelationalSourceDeclaration"/> from a class decorated
    /// with <see cref="RelationalSourceAttribute"/> that also implements
    /// <see cref="IRelationalSchemaProvider"/>.
    /// </summary>
    /// <remarks>
    /// Validation is strict and happens at startup rather than at registration,
    /// for the same reason as the credential schema: a blank tool key or engine
    /// would register a declaration the core cannot act on. That failure is
    /// invisible — extraction simply never happens and the query gate governs
    /// nothing — so it must surface as a refusal to start, not as silence.
    /// </remarks>
    /// <exception cref="ConnectorException">
    /// Thrown when <c>[RelationalSource]</c> is missing, the type is abstract or
    /// does not implement <see cref="IRelationalSchemaProvider"/>, or any of
    /// <c>Engine</c>, <c>DescribeTool</c>, <c>QueryTool</c> and <c>SqlArg</c> is
    /// blank.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <c>Scopes</c> has more than one entry and <c>DefaultScope</c>
    /// is blank, or when <c>DefaultScope</c> is set but is not one of
    /// <c>Scopes</c>. Same seam and same reasoning as
    /// <see cref="ConnectorHostBuilder.Build"/>'s credential-key check: refuse
    /// at startup, on the connector author's own deploy, rather than let an
    /// unqualified table name resolve ambiguously the first time a model calls
    /// the query tool in production.
    /// </exception>
    public static RelationalSourceDeclaration FromRelationalSourceType(Type t)
    {
        var sourceAttr = t.GetCustomAttributes(typeof(RelationalSourceAttribute), inherit: false)
                          .Cast<RelationalSourceAttribute>()
                          .FirstOrDefault()
                     ?? throw new ConnectorException(
                            $"Type {t.FullName} is missing the [RelationalSource] attribute.");

        if (!typeof(IRelationalSchemaProvider).IsAssignableFrom(t))
        {
            throw new ConnectorException(
                $"Type {t.FullName} is decorated with [RelationalSource] but does not implement " +
                $"{nameof(IRelationalSchemaProvider)}.");
        }

        // The parameterless-constructor requirement is enforced in Build(), and
        // only when the SDK has to construct the provider itself — a provider
        // holding a connection factory is supplied ready-made via
        // UseRelationalSchemaProvider.
        if (t.IsAbstract)
        {
            throw new ConnectorException(
                $"Relational schema provider {t.FullName} must be a concrete class.");
        }

        // Each message names the blank field: an operator reading a crash log
        // otherwise learns only that "something" was left out.
        //
        // Engine is checked for emptiness ONLY — deliberately no allowlist, and
        // not an oversight of the CredentialKinds.All parity above. The core
        // owns the set of supported engines and already rejects an unknown one
        // at first connect (ConnectorRegistryService::validateRelationalSource →
        // issue "relational_engine_unsupported", whose message names the allowed
        // set; Daemon prints every issue and exits 78). That diagnostic is
        // strictly better than one from here, and it ships with the core that
        // added the engine. An SDK-side copy of the list would instead refuse to
        // start a valid connector until the SDK is re-released — the SDK would
        // become the thing blocking an engine the platform already supports.
        RequireValue(t, nameof(RelationalSourceAttribute.Engine), sourceAttr.Engine,
            "the database engine behind this connector, e.g. \"sqlserver\"");
        RequireValue(t, nameof(RelationalSourceAttribute.DescribeTool), sourceAttr.DescribeTool,
            "the key of the tool that returns this source's canonical schema");
        RequireValue(t, nameof(RelationalSourceAttribute.QueryTool), sourceAttr.QueryTool,
            "the key of the SQL tool the core's query gate governs");
        RequireValue(t, nameof(RelationalSourceAttribute.SqlArg), sourceAttr.SqlArg,
            "which argument of QueryTool carries the SQL text");

        var scopes = sourceAttr.Scopes ?? Array.Empty<string>();
        ValidateScopes(scopes, sourceAttr.DefaultScope);

        return new RelationalSourceDeclaration(
            Engine:       sourceAttr.Engine,
            DescribeTool: sourceAttr.DescribeTool,
            QueryTool:    sourceAttr.QueryTool,
            SqlArg:       sourceAttr.SqlArg,
            Scopes:       scopes,
            // With exactly one scope the SOLE scope is the only place an
            // unqualified name can resolve, so declare it outright instead of
            // whatever the attribute happened to say (including nothing).
            // Kept identical to the PHP SDK so the same connector shape
            // declares the same thing in both languages; here both values are
            // compile-time constants, so unlike PHP this cannot drift per
            // environment — it is parity, not a fix.
            DefaultScope: scopes.Length == 1 ? scopes[0] : sourceAttr.DefaultScope,
            ProviderType: t,
            // Optional, unlike the RequireValue calls above: no such call here.
            // Requiring it would refuse every existing connector, all of which
            // declare no ParamsArg.
            ParamsArg: sourceAttr.ParamsArg);
    }

    private static void RequireValue(Type t, string field, string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ConnectorException(
                $"Relational schema provider {t.FullName} declares no {field} — {what}.");
        }
    }

    /// <summary>
    /// The forcing function: a relational source spanning more than one scope
    /// MUST name a DefaultScope, and a named DefaultScope must be one of the
    /// scopes actually declared.
    /// </summary>
    /// <remarks>
    /// Same seam and same reasoning as <see cref="ConnectorHostBuilder.Build"/>'s
    /// credential-key check: refuse at startup, on the connector author's own
    /// deploy, rather than let an unqualified table name resolve ambiguously
    /// (or not at all) the first time a model calls the query tool in
    /// production. Both checks below are purely local — no network, nothing to
    /// await — so there is no cost to running them here, before the worker
    /// ever dials the hub.
    ///
    /// Deliberately <see cref="ArgumentException"/> rather than this file's
    /// usual <see cref="ConnectorException"/>: the mistake here is a bad VALUE
    /// relationship between two fields the author supplied, not a missing
    /// declaration or a cross-reference to a tool that does not exist — the
    /// PHP SDK throws the equivalent generic argument exception
    /// (<c>InvalidArgumentException</c>) for this identical check, so a
    /// connector author moving between the two SDKs gets the same guidance.
    /// </remarks>
    private static void ValidateScopes(IReadOnlyList<string> scopes, string defaultScope)
    {
        var @default = defaultScope ?? "";

        if (scopes.Count > 1 && @default == "")
        {
            throw new ArgumentException(
                $"relational_source declares {scopes.Count} scopes but no default_scope. " +
                "An unqualified table name has to resolve somewhere, and only this connector knows " +
                $"where its connection points. Name one of: {string.Join(", ", scopes)}");
        }

        // Only meaningful with MORE THAN ONE scope. With zero or one there is
        // nothing to disambiguate, and the caller emits the sole scope rather
        // than this literal — so a mismatch changes nothing that ships.
        // Throwing on it would make an attribute constant naming the
        // PRODUCTION database break every deployment whose database is named
        // anything else, tests included, for no gain in correctness. Mirrors
        // the PHP SDK's DeclarationFactory::validateScopes().
        if (scopes.Count > 1 && @default != "" && !scopes.Contains(@default, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"relational_source default_scope `{@default}` is not one of the declared scopes: " +
                string.Join(", ", scopes));
        }
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
            Agents          = toolAttr.Agents ?? Array.Empty<string>(),
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
