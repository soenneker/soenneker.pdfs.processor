using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace Soenneker.Pdfs.Processor.Models;

/// <summary>
/// Defines text to locate and the value that should replace it.
/// </summary>
public sealed class PdfTextReplacement
{
    /// <summary>Gets the non-empty text to locate.</summary>
    [JsonPropertyName("search")]
    [JsonProperty("search")]
    public required string Search { get; init; }

    /// <summary>Gets the text that replaces each eligible match.</summary>
    [JsonPropertyName("replacement")]
    [JsonProperty("replacement")]
    public required string Replacement { get; init; }

    /// <summary>Gets the optional first one-based page on which the replacement is eligible.</summary>
    [JsonPropertyName("startPage")]
    [JsonProperty("startPage")]
    public int? StartPage { get; init; }

    /// <summary>Gets the optional last one-based page on which the replacement is eligible.</summary>
    [JsonPropertyName("endPage")]
    [JsonProperty("endPage")]
    public int? EndPage { get; init; }

    /// <summary>Gets the maximum number of matches to replace, or null to replace every eligible match.</summary>
    [JsonPropertyName("maximumReplacements")]
    [JsonProperty("maximumReplacements")]
    public int? MaximumReplacements { get; init; }
}
