using NJsonSchema;
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

        // Generate JSON schemas synchronously.
        var inputSchemaJson  = JsonSchema.FromType(argsType).ToJson();
        var outputSchemaJson = JsonSchema.FromType(resultType).ToJson();

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
