using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace Soenneker.Pdfs.Processor.Models;

/// <summary>
/// Identifies a PDF stream and an optional inclusive one-based page range to merge.
/// </summary>
public sealed class PdfMergeSource
{
    /// <summary>Gets the readable, seekable PDF stream. The processor does not dispose it.</summary>
    [JsonPropertyName("stream")]
    [JsonProperty("stream")]
    public required Stream Stream { get; init; }

    /// <summary>Gets the first one-based page to include, or null to begin with the first page.</summary>
    [JsonPropertyName("startPage")]
    [JsonProperty("startPage")]
    public int? StartPage { get; init; }

    /// <summary>Gets the last one-based page to include, or null to continue through the final page.</summary>
    [JsonPropertyName("endPage")]
    [JsonProperty("endPage")]
    public int? EndPage { get; init; }
}
