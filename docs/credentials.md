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

The declaration is the `[Credential]` and `[CredentialField]` attributes on your
handler class, described under [The declaration](#the-declaration) below.

```csharp
using VestedAI.ConnectorSdk.Credential;

[Credential(Kind = "basic", Title = "Al-Saif ERP account")]
[CredentialField(Key = "username", Label = "ERP username", Type = "text",     Required = true)]
[CredentialField(Key = "password", Label = "ERP password", Type = "password", Required = true)]
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

Register it, with the private key that opens sealed envelopes:

```csharp
// Program.cs
using System.Reflection;
using VestedAI.ConnectorSdk;

return await ConnectorHost.CreateBuilder()
    .ScanAssembly(Assembly.GetExecutingAssembly())
    .UseCredentialHandler(new ErpCredentials(erpClient))
    .Build()
    .RunFromEnvironmentAsync();
```

`UseCredentialHandler` lives on `ConnectorHostBuilder` — the object
`ConnectorHost.CreateBuilder()` returns, not the built `ConnectorApp`. Pass a
ready-made instance whenever the handler takes constructor dependencies, as this
one does: a scanned handler is constructed by the SDK with
`Activator.CreateInstance`, so `Build()` refuses a scanned handler that has no
parameterless constructor rather than failing at the first credential op. A
handler that genuinely has one needs no call at all — `ScanAssembly` finds it
through its `[Credential]` attribute.

`Build()` reads the keys from `VESTED_CREDENTIAL_PRIVATE_KEY` (or
`VESTED_CREDENTIAL_PRIVATE_KEY_FILE`) unless you supply them yourself with
`UseCredentialKeys(params string[] privateKeyPems)` — use that when they come
from a secret manager rather than the process environment. Registering a handler
without a key throws at startup rather than failing every credential check later
with a puzzling message.

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

Both attributes go **above the class**, one `[CredentialField]` per field, in
the order the user should see them. The platform builds the form from this —
you never write UI.

```csharp
[Credential(Kind = "basic", Title = "Al-Saif ERP account", HelpText = "Ask IT for a service login.")]
[CredentialField(Key = "username", Label = "ERP username", Type = "text",     Required = true)]
[CredentialField(Key = "password", Label = "ERP password", Type = "password", Required = true)]
[CredentialField(Key = "company",  Label = "Company",      Type = "select",
                 Options = new[] { "KSA", "UAE" })]
public sealed class ErpCredentials : IUserCredentialHandler
{
    // …
}
```

`Kind` is one of `basic`, `token`, `custom` (default `basic`). Field types are
`text`, `password`, `url`, `select` (default `text`). A `password` field renders
masked; `select` needs `Options`; `Label` defaults to `Key`.

Everything here is checked when the declaration is read — at `ScanAssembly` or
`UseCredentialHandler`, before your connector ever connects: a blank `Title`, an
unknown kind or type, a duplicate field key, an optionless `select` or a schema
with no fields at all throws `ConnectorException` at startup, because the
alternative is a rejected registration or a form the user cannot complete.

Registering a handler **without** `[Credential]` throws
(*"Type … is missing the [Credential] attribute."*). With no schema the platform
renders no form, so nobody can save a credential and none of your tools are
gated — every call keeps running as the connector's own shared account, which is
the misattribution this feature exists to end.

Put the attributes on the handler class **you register**. They are read with
`inherit: false`, so a subclass of an annotated handler declares nothing: the
scanner does not find it, and passing one to `UseCredentialHandler` throws the
missing-attribute error above.

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
