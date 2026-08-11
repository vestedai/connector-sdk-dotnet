using System.Reflection;
using VestedAI.ConnectorSdk.Tests.Runtime;
using VestedAI.ConnectorSdk.Credential;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Reflection;
using VestedAI.ConnectorSdk.Runtime;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

// ---------------------------------------------------------------------------
// Regression: SDK 0.4.2 shipped every credential building block but wired none
// of them to the runtime. There was no way to register a handler, the private
// key was read nowhere, Register.credential_schema was never populated, and
// Supervisor passed neither the opener nor the op dispatcher — so
// ctx.HasCredential() was always false and an inbound CredentialOpRequest was
// silently dropped.
//
// These tests pin the wiring end to end. They must keep passing in both
// directions: a connector that declares a schema gets the whole path, and one
// that declares none is left exactly as it was.
// ---------------------------------------------------------------------------

[Credential(
    Kind = "basic",
    Title = "Demo ERP account",
    HelpText = "Use your ERP sign-in.")]
[CredentialField(Key = "username", Label = "User name", Type = "text",
                 Placeholder = "j.smith")]
[CredentialField(Key = "password", Label = "Password", Type = "password")]
public sealed class DemoCredentialHandler : IUserCredentialHandler
{
    public Task<CredentialValidation> ValidateAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        => Task.FromResult(
            credential.TryGetValue("username", out var u)
                ? CredentialValidation.Succeeded(new Dictionary<string, string> { ["account"] = u })
                : CredentialValidation.Failed("No user name supplied."));

    public Task RevokeAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        => Task.CompletedTask;
}

// Not decorated with [Credential] — used to prove the attribute is what opts in.
public sealed class UndeclaredHandler : IUserCredentialHandler
{
    public Task<CredentialValidation> ValidateAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        => Task.FromResult(CredentialValidation.Succeeded());

    public Task RevokeAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        => Task.CompletedTask;
}

[Credential(Title = "Not a handler")]
[CredentialField(Key = "token")]
public sealed class CredNotAHandler { }

[Credential(Kind = "wat", Title = "Bad kind")]
[CredentialField(Key = "token")]
public sealed class BadKindHandler : UndeclaredHandlerBase { }

[Credential(Title = "Bad field type")]
[CredentialField(Key = "token", Type = "carrier-pigeon")]
public sealed class BadFieldTypeHandler : UndeclaredHandlerBase { }

[Credential(Title = "Select without options")]
[CredentialField(Key = "region", Type = "select")]
public sealed class SelectWithoutOptionsHandler : UndeclaredHandlerBase { }

[Credential(Title = "Duplicate keys")]
[CredentialField(Key = "token")]
[CredentialField(Key = "token")]
public sealed class DuplicateKeyHandler : UndeclaredHandlerBase { }

[Credential(Title = "No fields")]
public sealed class NoFieldsHandler : UndeclaredHandlerBase { }

[Credential(HelpText = "missing title")]
[CredentialField(Key = "token")]
public sealed class NoTitleHandler : UndeclaredHandlerBase { }

/// <summary>
/// A handler with a constructor dependency — the shape docs/credentials.md
/// shows. It can only be registered via UseCredentialHandler.
/// </summary>
[Credential(Title = "Dependent handler")]
[CredentialField(Key = "token", Type = "password")]
public sealed class DependentCredentialHandler : IUserCredentialHandler
{
    private readonly string _client;

    public DependentCredentialHandler(string client) => _client = client;

    public Task<CredentialValidation> ValidateAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        => Task.FromResult(CredentialValidation.Succeeded(
            new Dictionary<string, string> { ["client"] = _client }));

    public Task RevokeAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        => Task.CompletedTask;
}

/// <summary>Shared no-op base so the invalid fixtures stay short.</summary>
public abstract class UndeclaredHandlerBase : IUserCredentialHandler
{
    public Task<CredentialValidation> ValidateAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        => Task.FromResult(CredentialValidation.Succeeded());

    public Task RevokeAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        => Task.CompletedTask;
}

public class CredentialDeclarationTests
{
    [Fact]
    public void FromCredentialType_MapsKindTitleAndFieldsInOrder()
    {
        var decl = DeclarationFactory.FromCredentialType(typeof(DemoCredentialHandler));

        Assert.Equal("basic", decl.Kind);
        Assert.Equal("Demo ERP account", decl.Title);
        Assert.Equal("Use your ERP sign-in.", decl.HelpText);
        Assert.Equal(typeof(DemoCredentialHandler), decl.HandlerType);

        Assert.Equal(2, decl.Fields.Count);
        Assert.Equal("username", decl.Fields[0].Key);
        Assert.Equal("text", decl.Fields[0].Type);
        Assert.Equal("j.smith", decl.Fields[0].Placeholder);
        Assert.True(decl.Fields[0].Required);

        Assert.Equal("password", decl.Fields[1].Key);
        Assert.Equal("password", decl.Fields[1].Type);
    }

    [Theory]
    [InlineData(typeof(CredNotAHandler))]
    [InlineData(typeof(BadKindHandler))]
    [InlineData(typeof(BadFieldTypeHandler))]
    [InlineData(typeof(SelectWithoutOptionsHandler))]
    [InlineData(typeof(DuplicateKeyHandler))]
    [InlineData(typeof(NoFieldsHandler))]
    [InlineData(typeof(NoTitleHandler))]
    public void FromCredentialType_RejectsMalformedDeclarations(Type t)
        => Assert.Throws<ConnectorException>(() => DeclarationFactory.FromCredentialType(t));

    [Fact]
    public void ScanAssembly_FindsTheDeclaredHandler()
    {
        var (_, _, cred) = Scanner.ScanAssembly(
            new FakeAssembly(typeof(DemoCredentialHandler)));

        Assert.NotNull(cred);
        Assert.Equal(typeof(DemoCredentialHandler), cred.HandlerType);
    }

    [Fact]
    public void ScanAssembly_UndecoratedHandler_IsNotDiscovered()
    {
        // Implementing the interface is not opting in — the attribute is.
        var (_, _, cred) = Scanner.ScanAssembly(new FakeAssembly(typeof(UndeclaredHandler)));

        Assert.Null(cred);
    }

    [Fact]
    public void ScanAssembly_TwoHandlers_Throws()
        => Assert.Throws<ConnectorException>(() => Scanner.ScanAssembly(
            new FakeAssembly(typeof(DemoCredentialHandler), typeof(NoTitleHandler))));
}

public class CredentialKeyringTests
{
    private const string KeyA = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----";
    private const string KeyB = "-----BEGIN PRIVATE KEY-----\nBBBB\n-----END PRIVATE KEY-----";

    [Fact]
    public void Parse_SingleKey_ReturnsOne()
        => Assert.Single(CredentialKeyring.Parse(KeyA));

    [Fact]
    public void Parse_BlankLineSeparated_ReturnsBothNewestFirst()
    {
        var ring = CredentialKeyring.Parse($"{KeyA}\n\n{KeyB}\n");

        Assert.Equal(2, ring.Length);
        Assert.Contains("AAAA", ring[0]);
        Assert.Contains("BBBB", ring[1]);
    }

    [Fact]
    public void Parse_CrLfAndPaddedBlankLine_StillSplits()
    {
        var ring = CredentialKeyring.Parse($"{KeyA}\r\n   \r\n{KeyB}");

        Assert.Equal(2, ring.Length);
    }

    [Fact]
    public void Parse_Empty_ReturnsEmpty()
        => Assert.Empty(CredentialKeyring.Parse("   \n\n  "));
}

public class CredentialBuilderTests
{
    private static readonly FakeAssembly WithCredential = new(
        typeof(K5DemoAgent), typeof(K5DemoPingTool), typeof(DemoCredentialHandler));

    private static readonly FakeAssembly WithoutCredential = new(
        typeof(K5DemoAgent), typeof(K5DemoPingTool));

    // A real P-256 key, so CredentialOpener construction is exercised for real.
    private static string TestKey =>
        System.Text.Json.JsonDocument
            .Parse(File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "testdata", "credential-envelope-vectors.json")))
            .RootElement.GetProperty("connector_private_key_pkcs8_pem").GetString()!;

    [Fact]
    public void Build_HandlerDeclared_NoKey_ThrowsAtStartup()
    {
        // Failing here is deliberate: the alternative is every credential check
        // failing later with a message that does not name the cause.
        var ex = Assert.Throws<ConnectorException>(() =>
            ConnectorHost.CreateBuilder()
                .ScanAssembly(WithCredential)
                .UseCredentialKeys()          // explicitly empty — do not read the ambient env
                .Build());

        Assert.Contains("VESTED_CREDENTIAL_PRIVATE_KEY", ex.Message);
    }

    [Fact]
    public void Build_HandlerDeclaredWithKey_PopulatesSchemaHandlerAndOpener()
    {
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(WithCredential)
            .UseCredentialKeys(TestKey)
            .Build();

        Assert.NotNull(app.CredentialSchema);
        Assert.Equal("Demo ERP account", app.CredentialSchema.Title);
        Assert.IsType<DemoCredentialHandler>(app.CredentialHandler);
        Assert.NotNull(app.CredentialOpener);
    }

    [Fact]
    public void UseCredentialHandler_SuppliesAnInstanceWithDependencies()
    {
        // The handler has no parameterless constructor, so scanning alone
        // could not build it.
        var instance = new DependentCredentialHandler("erp-client");

        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(WithoutCredential)
            .UseCredentialHandler(instance)
            .UseCredentialKeys(TestKey)
            .Build();

        Assert.Same(instance, app.CredentialHandler);
        Assert.NotNull(app.CredentialSchema);
        Assert.Equal("Dependent handler", app.CredentialSchema.Title);
    }

    [Fact]
    public void Build_HandlerWithoutParameterlessCtor_AndNoInstance_Throws()
    {
        var ex = Assert.Throws<ConnectorException>(() =>
            ConnectorHost.CreateBuilder()
                .ScanAssembly(new FakeAssembly(typeof(DependentCredentialHandler)))
                .UseCredentialKeys(TestKey)
                .Build());

        Assert.Contains("UseCredentialHandler", ex.Message);
    }

    [Fact]
    public void UseCredentialHandler_ConflictingWithScannedType_Throws()
        => Assert.Throws<ConnectorException>(() =>
            ConnectorHost.CreateBuilder()
                .ScanAssembly(WithCredential)
                .UseCredentialHandler(new DependentCredentialHandler("erp-client")));

    [Fact]
    public void Build_NoHandler_LeavesEverythingNull()
    {
        // The no-credential path must stay a true no-op: this is what keeps
        // every existing connector ungated.
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(WithoutCredential)
            .Build();

        Assert.Null(app.CredentialSchema);
        Assert.Null(app.CredentialHandler);
        Assert.Null(app.CredentialOpener);
    }

    [Fact]
    public void ToProto_MapsEveryFieldOntoCredentialSchemaDecl()
    {
        var decl = DeclarationFactory.FromCredentialType(typeof(DemoCredentialHandler));
        var proto = Daemon.ToProto(decl);

        Assert.Equal("basic", proto.Kind);
        Assert.Equal("Demo ERP account", proto.Title);
        Assert.Equal("Use your ERP sign-in.", proto.HelpText);
        Assert.Equal(2, proto.Fields.Count);
        Assert.Equal("username", proto.Fields[0].Key);
        Assert.Equal("User name", proto.Fields[0].Label);
        Assert.Equal("j.smith", proto.Fields[0].Placeholder);
        Assert.True(proto.Fields[0].Required);
        Assert.Equal("password", proto.Fields[1].Type);
    }
}

public class CredentialOpDispatcherLazyIdTests
{
    private static readonly System.Text.Json.JsonElement Fixture = System.Text.Json.JsonDocument
        .Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "testdata", "credential-envelope-vectors.json")))
        .RootElement;

    private static System.Text.Json.JsonElement Vector => Fixture.GetProperty("vectors")[0];

    [Fact]
    public async Task DispatchAsync_ResolvesConnectorIdAtCallTime_NotConstructionTime()
    {
        // The dispatcher is built before HelloAck assigns the connector id.
        // Binding that id eagerly (as 0.4.2 did) makes the AAD check compare
        // against "" and reject every envelope.
        var identity = new SessionIdentity();

        var dispatcher = new CredentialOpDispatcher(
            new CredentialOpener(Fixture.GetProperty("connector_private_key_pkcs8_pem").GetString()!),
            new DemoCredentialHandler(),
            () => identity.ConnectorId);

        // HelloAck arrives only now.
        identity.ConnectorId = Vector.GetProperty("connector_id").GetString()!;

        var resp = await dispatcher.DispatchAsync(new Vested.V1.CredentialOpRequest
        {
            OpId         = "op-lazy",
            Op           = "validate",
            UserId       = Vector.GetProperty("user_id").GetString()!,
            UserEmail    = "j.smith@example.com",
            EnvelopeJson = Google.Protobuf.ByteString.CopyFromUtf8(
                Vector.GetProperty("envelope").GetRawText()),
            DeadlineMs   = 5000,
        });

        Assert.True(resp.Ok, resp.Error);
        Assert.Equal("j.smith", resp.Display.Fields["account"].StringValue);
    }
}
