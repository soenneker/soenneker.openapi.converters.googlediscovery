using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.OpenApi.Converters.GoogleDiscovery.Abstract;

/// <summary>Converts Google API Discovery REST documents into OpenAPI 3.1.0 JSON.</summary>
public interface IGoogleDiscoveryOpenApiConverter
{
    /// <summary>Converts a Discovery JSON document without performing network requests.</summary>
    /// <remarks>Uses flatPath when supplied, so resource names become individual path IDs. OAuth scopes are alternatives.
    /// Media upload/download methods and reserved templates without flatPath are rejected rather than silently misrepresented.</remarks>
    /// <exception cref="System.FormatException">The document contains invalid references, conflicting operations, or unsupported paths.</exception>
    /// <exception cref="System.NotSupportedException">The document requires media transfer support.</exception>
    string Convert(string discoveryJson);

    /// <summary>Reads a Discovery document and writes UTF-8 OpenAPI JSON, replacing the output file if present.</summary>
    /// <remarks>The input and output must be different files. Parent directories are created as needed.</remarks>
    ValueTask ConvertToFileAsync(string discoveryPath, string outputPath, CancellationToken cancellationToken = default);
}
