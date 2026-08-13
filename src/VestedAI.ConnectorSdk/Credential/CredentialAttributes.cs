namespace VestedAI.ConnectorSdk.Credential;

/// <summary>
/// Marks the class implementing <see cref="IUserCredentialHandler"/> as this
/// connector's credential handler, and declares the form the platform renders
/// for the user.
///
/// Declaring one is what turns per-user credentials on: a connector with no
/// <c>[Credential]</c> class registers no schema, is hidden from the credential
/// UI, and has none of its tools gated.
///
/// Apply once per assembly, together with one
/// <see cref="CredentialFieldAttribute"/> per field.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CredentialAttribute : Attribute
{
    /// <summary>One of <see cref="CredentialKinds.All"/>. Defaults to "basic".</summary>
    public string Kind { get; set; } = "basic";

    /// <summary>Form heading shown to the user (e.g. "Al-Saif ERP account").</summary>
    public string Title { get; set; } = "";

    /// <summary>Optional guidance shown under the heading.</summary>
    public string HelpText { get; set; } = "";
}

/// <summary>
/// Declares one field of the credential form. Multiple fields can be applied;
/// they are sent to the platform in declaration order.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CredentialFieldAttribute : Attribute
{
    /// <summary>
    /// Map key in the sealed field map — the key the handler and tools read
    /// (e.g. <c>credential["username"]</c>).
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>Field label shown to the user.</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// One of <see cref="CredentialFieldTypes.All"/>. Defaults to "text".
    /// A "password" field renders masked; "select" requires <see cref="Options"/>.
    /// </summary>
    public string Type { get; set; } = "text";

    /// <summary>Whether the user must supply a value. Defaults to true.</summary>
    public bool Required { get; set; } = true;

    /// <summary>Optional placeholder text.</summary>
    public string Placeholder { get; set; } = "";

    /// <summary>Choices for a "select" field. Ignored for every other type.</summary>
    public string[] Options { get; set; } = Array.Empty<string>();
}

/// <summary>Canonical values for <see cref="CredentialAttribute.Kind"/>.</summary>
public static class CredentialKinds
{
    /// <summary>All valid credential kinds.</summary>
    public static readonly string[] All = { "basic", "token", "custom" };
}

/// <summary>Canonical values for <see cref="CredentialFieldAttribute.Type"/>.</summary>
public static class CredentialFieldTypes
{
    /// <summary>All valid credential field types.</summary>
    public static readonly string[] All = { "text", "password", "url", "select" };
}

/// <summary>
/// Normalized representation of a single credential field, derived from
/// <see cref="CredentialFieldAttribute"/>.
/// </summary>
public sealed record CredentialFieldDeclaration(
    string Key,
    string Label,
    string Type,
    bool Required,
    string Placeholder,
    IReadOnlyList<string> Options);

/// <summary>
/// Normalized credential declaration derived from <see cref="CredentialAttribute"/>
/// and the <see cref="CredentialFieldAttribute"/>s on the same class.
/// </summary>
public sealed record CredentialDeclaration(
    string Kind,
    string Title,
    string HelpText,
    IReadOnlyList<CredentialFieldDeclaration> Fields,
    Type HandlerType);
