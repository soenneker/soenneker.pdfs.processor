using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace Soenneker.Pdfs.Processor.Models;

/// <summary>
/// Reports details about a completed PDF operation.
/// </summary>
public sealed class PdfProcessResult
{
    /// <summary>Gets the number of pages in the produced PDF.</summary>
    [JsonPropertyName("pageCount")]
    [JsonProperty("pageCount")]
    public required int PageCount { get; init; }

    /// <summary>Gets the combined source length in bytes when it can be determined.</summary>
    [JsonPropertyName("inputLength")]
    [JsonProperty("inputLength")]
    public required long? InputLength { get; init; }

    /// <summary>Gets the produced PDF length in bytes when it can be determined.</summary>
    [JsonPropertyName("outputLength")]
    [JsonProperty("outputLength")]
    public required long? OutputLength { get; init; }

    /// <summary>Gets the per-request text-replacement results.</summary>
    [JsonPropertyName("replacements")]
    [JsonProperty("replacements")]
    public IReadOnlyList<PdfTextReplacementResult> Replacements { get; init; } = [];
}
