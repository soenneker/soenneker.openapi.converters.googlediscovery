using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.OpenApi.Converters.GoogleDiscovery.Abstract;

namespace Soenneker.OpenApi.Converters.GoogleDiscovery;

public sealed class GoogleDiscoveryOpenApiConverter : IGoogleDiscoveryOpenApiConverter
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private static readonly Regex _template = new(@"\{([^{}]+)\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public string Convert(string discoveryJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discoveryJson);
        JsonObject source = JsonNode.Parse(discoveryJson) as JsonObject ?? throw new FormatException("Discovery document must be an object.");
        if (Text(source, "kind") != "discovery#restDescription" || Text(source, "protocol") != "rest")
            throw new FormatException("Expected a Google Discovery REST description.");
        string server = Text(source, "rootUrl") is { } root ? root.TrimEnd('/') + "/" + (Text(source, "servicePath") ?? "").TrimStart('/')
            : Required(source, "baseUrl");
        if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new FormatException("The API server must be an absolute HTTP URL.");

        JsonObject definitions = source["schemas"] as JsonObject ?? new JsonObject();
        var schemas = new JsonObject();
        foreach ((string name, JsonNode? schema) in definitions)
            schemas[name] = Schema(Object(schema), definitions);
        var scopes = new JsonObject();
        if (source["auth"]?["oauth2"]?["scopes"] is JsonObject scopeDefinitions)
            foreach ((string scope, JsonNode? value) in scopeDefinitions)
                scopes[scope] = Text(value, "description") ?? "";
        var securitySchemes = new JsonObject();
        if (scopes.Count > 0)
            securitySchemes["googleOAuth"] = new JsonObject
            {
                ["type"] = "oauth2",
                ["flows"] = new JsonObject { ["authorizationCode"] = new JsonObject
                {
                    ["authorizationUrl"] = "https://accounts.google.com/o/oauth2/v2/auth",
                    ["tokenUrl"] = "https://oauth2.googleapis.com/token", ["scopes"] = scopes
                }}
            };
        var paths = new JsonObject();
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        AddResources(source, source["parameters"] as JsonObject ?? new JsonObject(), definitions, scopes, paths, operationIds);
        var info = new JsonObject { ["title"] = Text(source, "title") ?? Required(source, "name"), ["version"] = Required(source, "version") };
        Copy(source, info, "description");
        var document = new JsonObject
        {
            ["openapi"] = "3.1.0", ["info"] = info,
            ["servers"] = new JsonArray(new JsonObject { ["url"] = server.TrimEnd('/') }),
            ["paths"] = paths, ["components"] = new JsonObject { ["schemas"] = schemas, ["securitySchemes"] = securitySchemes }
        };
        return document.ToJsonString(_jsonOptions);
    }

    public async ValueTask ConvertToFileAsync(string discoveryPath, string outputPath, CancellationToken cancellationToken = default)
    {
        string input = Path.GetFullPath(discoveryPath);
        string output = Path.GetFullPath(outputPath);
        if (string.Equals(input, output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Input and output paths must differ.", nameof(outputPath));
        string result = Convert(await File.ReadAllTextAsync(input, cancellationToken));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, result, cancellationToken);
    }

    private static void AddResources(JsonObject resource, JsonObject common, JsonObject definitions, JsonObject scopes, JsonObject paths,
        HashSet<string> operationIds)
    {
        if (resource["methods"] is JsonObject methods)
            foreach ((_, JsonNode? value) in methods)
            {
                JsonObject method = Object(value);
                string id = Required(method, "id");
                if (!operationIds.Add(id)) throw new FormatException($"Duplicate operation ID: {id}");
                if (Flag(method, "supportsMediaUpload") || Flag(method, "supportsMediaDownload"))
                    throw new NotSupportedException($"Media transfer is not supported: {id}");
                string originalPath = Required(method, "path");
                string path = "/" + (Text(method, "flatPath") ?? originalPath).TrimStart('/');
                var pathNames = _template.Matches(path).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                if (pathNames.Any(n => !Regex.IsMatch(n, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))
                    throw new FormatException($"Unsupported URI template in {id}: {path}. A flatPath is required.");
                string verb = Required(method, "httpMethod").ToLowerInvariant();
                if (verb is not ("get" or "put" or "post" or "delete" or "options" or "head" or "patch" or "trace"))
                    throw new FormatException($"Unsupported HTTP method: {verb}");
                JsonObject item = paths[path] as JsonObject ?? new JsonObject();
                if (item.ContainsKey(verb)) throw new FormatException($"Conflicting operation: {verb} {path}");
                if (item.Parent is null) paths[path] = item;
                var operation = new JsonObject { ["operationId"] = id };
                Copy(method, operation, "description", "deprecated");
                var parameters = new JsonArray();
                var merged = (JsonObject)common.DeepClone();
                if (method["parameters"] is JsonObject local)
                    foreach ((string name, JsonNode? parameter) in local) merged[name] = parameter?.DeepClone();
                foreach ((string name, JsonNode? valueParameter) in merged)
                {
                    JsonObject parameter = Object(valueParameter);
                    string location = Text(parameter, "location") ?? "query";
                    if (location == "path" && !pathNames.Contains(name)) continue;
                    if (location is not ("path" or "query")) throw new FormatException($"Unsupported parameter location: {location}");
                    var converted = new JsonObject
                    {
                        ["name"] = name, ["in"] = location, ["required"] = location == "path" || Flag(parameter, "required"),
                        ["schema"] = Schema(parameter, definitions)
                    };
                    Copy(parameter, converted, "description", "deprecated");
                    if (Flag(parameter, "repeated")) { converted["style"] = "form"; converted["explode"] = true; }
                    parameters.Add((JsonNode)converted);
                }
                foreach (string name in pathNames)
                    if (!merged.TryGetPropertyValue(name, out JsonNode? existing) || Text(existing, "location") != "path")
                        parameters.Add((JsonNode)new JsonObject { ["name"] = name, ["in"] = "path", ["required"] = true,
                            ["description"] = $"Resource ID for {name} in {originalPath}.", ["schema"] = new JsonObject { ["type"] = "string" } });
                if (parameters.Count > 0) operation["parameters"] = parameters;
                if (method["request"] is JsonObject request)
                    operation["requestBody"] = new JsonObject { ["required"] = true, ["content"] = Content(Schema(request, definitions)) };
                operation["responses"] = method["response"] is JsonObject response
                    ? new JsonObject { ["200"] = new JsonObject { ["description"] = "Successful response", ["content"] = Content(Schema(response, definitions)) } }
                    : new JsonObject { ["204"] = new JsonObject { ["description"] = "Successful response with no body" } };
                if (method["scopes"] is JsonArray methodScopes && methodScopes.Count > 0)
                {
                    var security = new JsonArray();
                    foreach (JsonNode? scope in methodScopes)
                    {
                        string scopeName = scope!.GetValue<string>();
                        if (!scopes.ContainsKey(scopeName)) throw new FormatException($"Undefined OAuth scope: {scopeName}");
                        security.Add((JsonNode)new JsonObject { ["googleOAuth"] = new JsonArray(JsonValue.Create(scopeName)) });
                    }
                    operation["security"] = security;
                }
                item[verb] = operation;
            }
        if (resource["resources"] is JsonObject children)
            foreach ((_, JsonNode? child) in children) AddResources(Object(child), common, definitions, scopes, paths, operationIds);
    }

    private static JsonObject Content(JsonObject schema) => new() { ["application/json"] = new JsonObject { ["schema"] = schema } };

    private static JsonObject Schema(JsonObject source, JsonObject definitions)
    {
        var result = new JsonObject();
        if (Text(source, "$ref") is { } reference)
        {
            if (!definitions.ContainsKey(reference)) throw new FormatException($"Undefined schema: {reference}");
            // OpenAPI 3.1 schema references support sibling metadata directly.
            result["$ref"] = "#/components/schemas/" + reference.Replace("~", "~0").Replace("/", "~1");
        }
        else
        {
            string type = Text(source, "type") ?? "object";
            if (type == "any") { }
            else if (type is "object" or "array" or "string" or "integer" or "number" or "boolean") result["type"] = type;
            else throw new FormatException($"Unsupported schema type: {type}");
            string? format = Text(source, "format");
            if (format is not null)
            {
                // Discovery int64/uint64 values are JSON strings; never turn them into JSON numbers.
                if (type == "string" && format is "int64" or "uint64") result["x-google-format"] = format;
                else if (format == "google-datetime") result["format"] = "date-time";
                else if (format == "uint32" && type == "integer")
                {
                    result["format"] = "int64";
                    result["minimum"] = 0;
                    result["maximum"] = 4294967295L;
                }
                else if (format is "date" or "date-time" or "byte" or "int32" or "int64" or "float" or "double") result["format"] = format;
                else result["x-google-format"] = format;
            }
            if (source["properties"] is JsonObject properties)
            {
                var converted = new JsonObject();
                var required = new JsonArray();
                foreach ((string name, JsonNode? property) in properties)
                {
                    converted[name] = Schema(Object(property), definitions);
                    if (Flag(property, "required")) required.Add((JsonNode)JsonValue.Create(name)!);
                }
                result["properties"] = converted;
                if (required.Count > 0) result["required"] = required;
            }
            if (source["items"] is JsonObject items) result["items"] = Schema(items, definitions);
            else if (type == "array") throw new FormatException("Array schemas must specify items.");
            if (source["additionalProperties"] is JsonObject additional) result["additionalProperties"] = Schema(additional, definitions);
            else if (source["additionalProperties"] is JsonValue additionalFlag) result["additionalProperties"] = additionalFlag.DeepClone();
            foreach (string bound in new[] { "minimum", "maximum" })
                if (source[bound] is { } numeric && type is "integer" or "number")
                    result[bound] = decimal.Parse(numeric.ToString(), CultureInfo.InvariantCulture);
            if (source["default"] is { } defaultValue) result["default"] = TypedValue(defaultValue, type);
            if (source["enum"] is JsonArray values)
            {
                var converted = new JsonArray();
                foreach (JsonNode? value in values) converted.Add(TypedValue(value!, type));
                result["enum"] = converted;
            }
        }
        Copy(source, result, "description", "pattern", "readOnly", "deprecated");
        if (source["annotations"] is { } annotations) result["x-google-annotations"] = annotations.DeepClone();
        CopyExtension(source, result, "enumDescriptions");
        CopyExtension(source, result, "enumDeprecated");
        if (Flag(source, "repeated")) return new JsonObject { ["type"] = "array", ["items"] = result };
        return result;
    }

    private static JsonNode TypedValue(JsonNode value, string type) => type switch
    {
        "boolean" => JsonValue.Create(bool.Parse(value.ToString()))!,
        "integer" => JsonValue.Create(long.Parse(value.ToString(), CultureInfo.InvariantCulture))!,
        "number" => JsonValue.Create(decimal.Parse(value.ToString(), CultureInfo.InvariantCulture))!,
        _ => value.DeepClone()
    };

    private static void CopyExtension(JsonObject source, JsonObject target, string name)
    {
        if (source[name] is { } value) target["x-google-" + name] = value.DeepClone();
    }
    private static void Copy(JsonObject source, JsonObject target, params string[] names)
    {
        foreach (string name in names) if (source[name] is { } value) target[name] = value.DeepClone();
    }
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw new FormatException("Expected a JSON object.");
    private static string? Text(JsonNode? node, string name) => node?[name]?.GetValue<string>();
    private static string Required(JsonNode node, string name) => Text(node, name) is { Length: > 0 } value ? value : throw new FormatException($"Missing {name}.");
    private static bool Flag(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue(out bool flag) && flag;
}
