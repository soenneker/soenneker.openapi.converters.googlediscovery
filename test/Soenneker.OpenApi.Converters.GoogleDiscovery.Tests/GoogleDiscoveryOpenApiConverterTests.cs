using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Soenneker.OpenApi.Converters.GoogleDiscovery.Tests;

public sealed class GoogleDiscoveryOpenApiConverterTests
{
    private readonly GoogleDiscoveryOpenApiConverter _converter = new();

    [Test]
    [Arguments("meet.v2.json")]
    [Arguments("calendar.v3.json")]
    public void ConvertsEveryDiscoveryOperation(string fixture)
    {
        string input = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        JsonNode discovery = JsonNode.Parse(input)!;
        JsonNode output = JsonNode.Parse(_converter.Convert(input))!;
        Check(output["openapi"]!.GetValue<string>() == "3.1.0", "OpenAPI version");
        string[] expected = Operations(discovery).Order().ToArray();
        string[] actual = output["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject())
            .Select(p => p.Value!["operationId"]!.GetValue<string>()).Order().ToArray();
        Check(expected.SequenceEqual(actual), "Every operation must survive conversion exactly once");
        Check(output["components"]!["schemas"]!.AsObject().Count == discovery["schemas"]!.AsObject().Count, "Every schema must survive");
        foreach (var path in output["paths"]!.AsObject())
            Check(!path.Key.Contains("{+"), "Reserved expansions must be flattened");
    }

    [Test]
    public void PreservesMeetPathsAndOAuthAlternatives()
    {
        JsonNode output = Fixture("meet.v2.json");
        JsonNode get = output["paths"]!["/v2/spaces/{spacesId}"]!["get"]!;
        Check(get["parameters"]!.AsArray().Any(p => p!["name"]!.GetValue<string>() == "spacesId" && p["required"]!.GetValue<bool>()), "Flattened path ID");
        Check(!get["parameters"]!.AsArray().Any(p => p!["name"]!.GetValue<string>() == "name"), "Original resource-name parameter must be removed");
        Check(get["security"]!.AsArray().Count > 1, "Scopes are OR alternatives, not an AND requirement");
        Check(output["components"]!["schemas"]!["ConferenceRecord"]!["properties"]!["startTime"]!["format"]!.GetValue<string>() == "date-time", "Google timestamps");
        Check(output["paths"]!["/v2/spaces/{spacesId}:endActiveConference"]!["post"]!["requestBody"] is not null, "Custom action body");
    }

    [Test]
    public void PreservesCalendarWireTypesAndQueryParameters()
    {
        JsonNode output = Fixture("calendar.v3.json");
        Check(output["servers"]![0]!["url"]!.GetValue<string>() == "https://www.googleapis.com/calendar/v3", "Calendar base path");
        JsonNode list = output["paths"]!["/calendars/{calendarId}/events"]!["get"]!;
        JsonNode max = list["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "maxResults")!;
        Check(max["schema"]!["default"]!.GetValue<int>() == 250, "Numeric defaults must be numbers");
        JsonNode privateProperties = output["components"]!["schemas"]!["Event"]!["properties"]!["extendedProperties"]!["properties"]!["private"]!;
        Check(privateProperties["additionalProperties"]!["type"]!.GetValue<string>() == "string", "Map value schema");
        Check(output["components"]!["schemas"]!["Channel"]!["properties"]!["expiration"]!["type"]!.GetValue<string>() == "string", "int64 wire strings");
    }

    [Test]
    public void ConvertsRepeatedParametersAndRecursiveReferences()
    {
        JsonNode input = Minimal();
        input["schemas"] = JsonNode.Parse("""{"Node":{"type":"object","properties":{"next":{"$ref":"Node","description":"Next node"},"enabled":{"type":"boolean","required":true}}}}""");
        input["parameters"] = JsonNode.Parse("""{"ids":{"type":"integer","format":"int32","repeated":true,"location":"query","default":"2"}}""");
        JsonNode output = JsonNode.Parse(_converter.Convert(input.ToJsonString()))!;
        JsonNode parameter = output["paths"]!["/items"]!["get"]!["parameters"]![0]!;
        Check(parameter["explode"]!.GetValue<bool>(), "Repeated query parameters");
        Check(parameter["schema"]!["items"]!["default"]!.GetValue<int>() == 2, "Typed repeated default");
        JsonNode schema = output["components"]!["schemas"]!["Node"]!;
        Check(schema["properties"]!["next"]!["$ref"]!.GetValue<string>() == "#/components/schemas/Node", "Recursive reference");
        Check(schema["properties"]!["next"]!["description"]!.GetValue<string>() == "Next node", "Reference sibling metadata");
        Check(schema["required"]![0]!.GetValue<string>() == "enabled", "Required property");
    }

    [Test]
    public void RejectsUndefinedReferencesAndUnsupportedMedia()
    {
        JsonNode input = Minimal();
        input["methods"]!["list"]!["response"] = JsonNode.Parse("""{"$ref":"Missing"}""");
        Expect<FormatException>(() => _converter.Convert(input.ToJsonString()));
        input["methods"]!["list"]!.AsObject().Remove("response");
        input["methods"]!["list"]!["supportsMediaUpload"] = true;
        Expect<NotSupportedException>(() => _converter.Convert(input.ToJsonString()));
    }

    [Test]
    public void RejectsConflictingOperationsAndUnflattenedReservedPaths()
    {
        JsonNode input = Minimal();
        input["methods"]!["list"]!["path"] = "v1/{+name}";
        Expect<FormatException>(() => _converter.Convert(input.ToJsonString()));
        input["methods"]!["list"]!["path"] = "items";
        input["methods"]!["other"] = input["methods"]!["list"]!.DeepClone();
        input["methods"]!["other"]!["id"] = "other";
        Expect<FormatException>(() => _converter.Convert(input.ToJsonString()));
    }

    [Test]
    public async Task WritesFilesAndProtectsInput()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "input.json");
            string output = Path.Combine(directory, "output", "openapi.json");
            await File.WriteAllTextAsync(input, Minimal().ToJsonString());
            await _converter.ConvertToFileAsync(input, output);
            Check(File.Exists(output), "Output file");
            try { await _converter.ConvertToFileAsync(input, input); throw new InvalidOperationException("Expected input protection"); }
            catch (ArgumentException) { }
        }
        finally { Directory.Delete(directory, true); }
    }

    private JsonNode Fixture(string name) => JsonNode.Parse(_converter.Convert(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))))!;
    private static JsonNode Minimal() => JsonNode.Parse("""{"kind":"discovery#restDescription","protocol":"rest","name":"test","version":"v1","rootUrl":"https://example.com/","servicePath":"","methods":{"list":{"id":"list","httpMethod":"GET","path":"items"}}}""")!;
    private static System.Collections.Generic.IEnumerable<string> Operations(JsonNode node)
    {
        if (node["methods"] is JsonObject methods)
            foreach (var method in methods) yield return method.Value!["id"]!.GetValue<string>();
        if (node["resources"] is JsonObject resources)
            foreach (var resource in resources)
                foreach (string id in Operations(resource.Value!)) yield return id;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
