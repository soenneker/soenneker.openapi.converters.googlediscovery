using System;
using System.Linq;
using Microsoft.OpenApi;
using Soenneker.OpenApi.Converters.GoogleDiscovery;

if (args.Length != 2) throw new ArgumentException("Usage: Verify <discovery.json> <openapi.json>");
await new GoogleDiscoveryOpenApiConverter().ConvertToFileAsync(args[0], args[1]);
var result = await OpenApiDocument.LoadAsync(args[1]);
if (result.Diagnostic.Errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostic.Errors));
var errors = result.Document.Validate(ValidationRuleSet.GetDefaultRuleSet()).ToArray();
if (errors.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
Console.WriteLine($"Generated and validated {args[1]}: {result.Document.Paths.Values.Sum(p => p.Operations.Count)} operations, {result.Document.Components.Schemas.Count} schemas.");
