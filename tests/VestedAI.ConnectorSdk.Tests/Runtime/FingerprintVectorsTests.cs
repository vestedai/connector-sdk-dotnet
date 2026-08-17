using System.Text.Json;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

/// <summary>
/// The baseline fingerprint is a CROSS-SDK contract.
///
/// dotnet, node and python canonicalise the same structure and the hub uses the
/// result to decide whether a connector changed. Nothing checked they agreed,
/// and two things did not: the sort comparer (locale vs culture vs ordinal) and
/// <c>model_config</c> (this SDK emitted null where the other two emit {}).
///
/// This fixture is the check. <c>vested-ai-sdks/testdata</c> is canonical and
/// each SDK carries a generated copy, which
/// <c>scripts/verify-fingerprint-vectors.sh</c> guards against drift.
///
/// php is deliberately NOT in this set: its canonical form nests tools inside
/// agent declarations and has never been comparable with these three.
/// </summary>
public class FingerprintVectorsTests
{
    private sealed record Vector(string Name, string ExpectedSha256,
                                 List<AgentDeclaration> Agents,
                                 Dictionary<string, ToolDeclaration> Tools);

    public static TheoryData<string> VectorNames()
    {
        var data = new TheoryData<string>();
        foreach (var v in Load()) data.Add(v.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void FingerprintMatchesCrossSdkVector(string name)
    {
        var vector = Load().Single(v => v.Name == name);

        Assert.Equal(
            vector.ExpectedSha256,
            Fingerprint.Compute(vector.Agents, vector.Tools));
    }

    private static string VectorsPath()
    {
        // Walk up from the test binary to the SDK root, where testdata/ lives.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "testdata", "fingerprint-vectors.json");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new FileNotFoundException("testdata/fingerprint-vectors.json not found above " + AppContext.BaseDirectory);
    }

    private static List<Vector> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorsPath()));
        var vectors = new List<Vector>();

        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var agents = new List<AgentDeclaration>();
            foreach (var a in v.GetProperty("agents").EnumerateArray())
            {
                var instructions = new List<InstructionDeclaration>();
                foreach (var i in a.GetProperty("instructions").EnumerateArray())
                {
                    instructions.Add(new InstructionDeclaration(
                        i.GetProperty("type").GetString()!,
                        i.GetProperty("position").GetInt32(),
                        i.GetProperty("body").GetString()!,
                        i.GetProperty("format").GetString()!));
                }

                agents.Add(new AgentDeclaration(
                    Key:          a.GetProperty("key").GetString()!,
                    Name:         a.GetProperty("name").GetString()!,
                    Model:        a.GetProperty("model").GetString()!,
                    Description:  a.GetProperty("description").GetString()!,
                    Status:       a.GetProperty("status").GetString()!,
                    Instructions: instructions));
            }

            var tools = new Dictionary<string, ToolDeclaration>(StringComparer.Ordinal);
            foreach (var t in v.GetProperty("tools").EnumerateArray())
            {
                var key = t.GetProperty("key").GetString()!;
                var output = t.GetProperty("output_schema");
                tools[key] = new ToolDeclaration
                {
                    Key               = key,
                    Name              = t.GetProperty("name").GetString()!,
                    Description       = t.GetProperty("description").GetString()!,
                    Sensitivity       = t.GetProperty("sensitivity").GetString()!,
                    DefaultDeadlineMs = t.GetProperty("default_deadline_ms").GetInt32(),
                    MaxResultBytes    = t.GetProperty("max_result_bytes").GetInt32(),
                    InputSchemaJson   = t.GetProperty("input_schema").GetRawText(),
                    OutputSchemaJson  = output.ValueKind == JsonValueKind.Null ? null : output.GetRawText(),
                    HandlerType       = typeof(object),
                    ArgsType          = typeof(object),
                    ResultType        = typeof(object),
                };
            }

            vectors.Add(new Vector(
                v.GetProperty("name").GetString()!,
                v.GetProperty("expected_sha256").GetString()!,
                agents,
                tools));
        }

        return vectors;
    }
}
