using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>
/// The specs' vectors, by their path under testdata: msfp/v0 holds copies of
/// msfp-spec/vectors/v0 (MSFP's), media/v0 copies of msfp-media/vectors/v0
/// (MSFP Media's).
/// </summary>
internal static class TestVectors
{
    public static JsonObject Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", name);
        return JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
    }

    /// <summary>The cases of one section, each an object.</summary>
    public static List<JsonObject> Cases(string file, string section)
    {
        var list = Load(file)[section]?.AsArray() ?? throw new InvalidOperationException($"{file}: no {section}");
        Assert.NotEmpty(list);
        return list.Select(c => c!.AsObject()).ToList();
    }

    public static string Str(this JsonObject c, string name) => c[name]?.GetValue<string>() ?? "";

    public static bool Valid(this JsonObject c) => c["valid"]!.GetValue<bool>();

    public static byte[] Hex(this JsonObject c) => Convert.FromHexString(c.Str("hex"));

    /// <summary>Fails listing every failing case, with how many there were.</summary>
    public static void NoFailures(List<string> failures, int total, string what)
    {
        Assert.True(failures.Count == 0,
            $"{what}: {failures.Count} of {total} cases failed:\n  " + string.Join("\n  ", failures));
    }
}
