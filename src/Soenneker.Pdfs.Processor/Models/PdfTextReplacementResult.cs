using Soenneker.Pdfs.Processor.Enums;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace Soenneker.Pdfs.Processor.Models;

/// <summary>
/// Reports the outcome of one requested replacement.
/// </summary>
public sealed class PdfTextReplacementResult
{
    /// <summary>Gets the requested search text.</summary>
    [JsonPropertyName("search")]
    [JsonProperty("search")]
    public required string Search { get; init; }

    /// <summary>Gets the requested replacement text.</summary>
    [JsonPropertyName("replacement")]
    [JsonProperty("replacement")]
    public required string Replacement { get; init; }

    /// <summary>Gets the replacement outcome.</summary>
    [JsonPropertyName("status")]
    [JsonProperty("status")]
    public required PdfReplacementStatus Status { get; init; }

    /// <summary>Gets the number of text matches that were replaced.</summary>
    [JsonPropertyName("replacementCount")]
    [JsonProperty("replacementCount")]
    public required int ReplacementCount { get; init; }

    /// <summary>Gets the number of replacements whose matches crossed two or more PDF string fragments.</summary>
    [JsonPropertyName("crossFragmentReplacementCount")]
    [JsonProperty("crossFragmentReplacementCount")]
    public int CrossFragmentReplacementCount { get; init; }

    /// <summary>Gets the ordered one-based page numbers on which replacements occurred.</summary>
    [JsonPropertyName("pageNumbers")]
    [JsonProperty("pageNumbers")]
    public required IReadOnlyList<int> PageNumbers { get; init; }

    /// <summary>Gets whether replacement text was written into the original text operands without changing their surrounding font operators.</summary>
    [JsonPropertyName("fontResourcePreserved")]
    [JsonProperty("fontResourcePreserved")]
    public required bool FontResourcePreserved { get; init; }

    /// <summary>Gets an optional explanation of the replacement outcome.</summary>
    [JsonPropertyName("message")]
    [JsonProperty("message")]
    public string? Message { get; init; }
}
