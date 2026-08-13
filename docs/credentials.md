# Per-user credentials

Some integrations act on behalf of the person asking, not on behalf of the
connector. An ERP that enforces its own permissions is the clearest case: if
every call arrives as one service account, the ERP's ACLs do nothing and its
audit log names a robot instead of a human.

Per-user credentials fix that. Each user stores their own credentials for your
integration; the platform hands them to your worker on every tool call.

**The platform cannot read them.** Credentials are sealed in the user's browser
with a public key generated for your connector. The private half lives only on
your worker. A full database dump of the platform leaks nothing.

---

## Opting in

A connector declares a credential schema at registration. Declaring one is what
turns the whole feature on for your integration — a connector that declares
nothing is unaffected in every respect.

```csharp
using VestedAI.ConnectorSdk.Credential;

[Credential(Kind = "basic", Title = "Al-Saif ERP account",
            HelpText = "Use the sign-in you use for the ERP itself.")]
[CredentialField(Key = "username", Label = "User name", Type = "text")]
[CredentialField(Key = "password", Label = "Password",  Type = "password")]
public sealed class ErpCredentials : IUserCredentialHandler
{
    private readonly ErpClient _erp;

    public ErpCredentials(ErpClient erp) => _erp = erp;

    public async Task<CredentialValidation> ValidateAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
    {
        var who = await _erp.WhoAmIAsync(credential["username"], credential["password"]);

        return who is null
            ? CredentialValidation.Failed("ERP rejected those credentials.")
            : CredentialValidation.Succeeded(new Dictionary<string, string>
              {
                  ["account"] = who.Login,
                  ["role"]    = who.Role,
              });
    }

    // Optional: tear down a remote session. Best-effort.
    public Task RevokeAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential) =>
        Task.CompletedTask;
}
```
`ScanAssembly` picks the handler up the same way it picks up your agents and
tools — the `[Credential]` attribute is the registration. Nothing else in
`Program.cs` changes:

```csharp
return await ConnectorHost.CreateBuilder()
    .ScanAssembly(Assembly.GetExecutingAssembly())
    .Build()
    .RunFromEnvironmentAsync();
```

Keys come from `VESTED_CREDENTIAL_PRIVATE_KEY` (or `VESTED_CREDENTIAL_PRIVATE_KEY_FILE`)
when you don't pass them explicitly. Registering a handler without a key throws
at startup rather than failing every credential check later with a puzzling
message.

Two escape hatches, for when the defaults don't fit:

```csharp
ConnectorHost.CreateBuilder()
    .ScanAssembly(Assembly.GetExecutingAssembly())
    // Keys from a secret manager rather than the environment.
    .UseCredentialKeys(pemNewest, pemPrevious)
    // A handler with constructor dependencies, built by you.
    .UseCredentialHandler(new ErpCredentials(erpClient))
    .Build();
```

Implementing `IUserCredentialHandler` is not what opts you in — the
`[Credential]` attribute is. A class that implements the interface without the
attribute is ignored, and a connector that declares no `[Credential]` class
sends no `credential_schema` at registration, which is what keeps its tools
ungated.

## Using them in a tool

```csharp
public async Task<object> HandleAsync(Args args, ToolContext ctx)
{
    var creds = ctx.Credential();      // { ["username"] = "…", ["password"] = "…" }

    return await _erp.SearchAsUserAsync(creds["username"], creds["password"], args.Q);
}
```

`Credential()` is lazy and memoized: a tool that never calls it never pays for a
decrypt, and calling it twice costs one key agreement.

Use `ctx.HasCredential()` if a tool works with or without one.

## What the SDK guarantees

**An envelope sealed for another user throws.** Every envelope is
cryptographically bound to the connector and the user it was sealed for, and
the SDK verifies that binding before handing you plaintext. You cannot
accidentally serve user A's request with user B's credentials — the check is
inside `credential()`, not something you remember to call.

**A tool call without a usable credential never reaches you.** The platform
refuses it and tells the user what to do. By the time your handler runs, the
credential is present and valid.

## The declaration

Field types are `text`, `password`, `url`, `select`. A `password` field renders
masked; `select` needs `options`. The platform builds the user's form from this
— you never write UI.

`[Credential]` carries `Kind` (`basic`, `token` or `custom`), `Title` and
`HelpText`; one `[CredentialField]` follows per field:

```csharp
[Credential(Kind = "custom", Title = "Warehouse login")]
[CredentialField(Key = "username", Label = "User name",  Type = "text",
                 Placeholder = "j.smith")]
[CredentialField(Key = "token",    Label = "API token",  Type = "password")]
[CredentialField(Key = "region",   Label = "Region",     Type = "select",
                 Options = new[] { "eu-west", "me-central" })]
[CredentialField(Key = "endpoint", Label = "Endpoint",   Type = "url",
                 Required = false)]
public sealed class WarehouseCredentials : IUserCredentialHandler { /* … */ }
```

`Key` is the map key your handler and tools read — `credential["username"]`.
`Required` defaults to true. The declaration is validated when the builder runs,
so a `select` with no `Options`, a duplicate key, or an unknown type fails at
startup rather than producing a form the user cannot complete.

## Key rotation

An operator can rotate your connector's keypair. Envelopes sealed under the old
key stop being readable, so affected users are asked to re-enter.

To ride out the overlap, keep both keys in the ring — newest first, separated by
a blank line in `VESTED_CREDENTIAL_PRIVATE_KEY`. The SDK tries each in turn.

## Things worth knowing

- **`display` is shown to the user.** Put an account name or role in it, never
  the credential.
- **Error text from `failed()` is shown verbatim.** Don't include stack traces
  or internal hostnames.
- **Automated runs need an owner.** A scheduled workflow uses the credentials of
  the person who owns it. A workflow instance with no owner at all is refused
  rather than run as an arbitrary employee.
