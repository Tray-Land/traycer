using System.Text.Json.Nodes;

namespace TrayCer.Tests;

internal static class Sample
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    public static JsonNode Json(string name) => JsonNode.Parse(Text(name))!;
}
