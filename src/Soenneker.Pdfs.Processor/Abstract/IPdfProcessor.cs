using Soenneker.Pdfs.Processor.Models;
using Soenneker.Pdfs.Processor.Options;

namespace Soenneker.Pdfs.Processor.Abstract;

/// <summary>
/// Provides structural PDF merging, text-run replacement, cleanup, and compression operations.
/// </summary>
public interface IPdfProcessor
{
    /// <summary>Merges pages from the supplied PDF sources without rasterizing them.</summary>
    /// <param name="sources">Ordered PDF sources and optional page ranges.</param>
    /// <param name="destination">The writable destination stream. The caller retains ownership.</param>
    /// <param name="options">Optional merge and final-output settings.</param>
    /// <param name="cancellationToken">Token used to cancel processing.</param>
    /// <returns>Details about the produced document.</returns>
    ValueTask<PdfProcessResult> Merge(IReadOnlyCollection<PdfMergeSource> sources, Stream destination, PdfMergeOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Merges PDF files and atomically replaces the destination after processing succeeds.</summary>
    /// <param name="sourcePaths">Ordered paths of PDF files to merge.</param>
    /// <param name="destinationPath">The PDF file to create or replace.</param>
    /// <param name="options">Optional merge and final-output settings.</param>
    /// <param name="cancellationToken">Token used to cancel processing.</param>
    /// <returns>Details about the produced document.</returns>
    ValueTask<PdfProcessResult> MergeFiles(IReadOnlyCollection<string> sourcePaths, string destinationPath, PdfMergeOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces text contained in individual PDF text-showing operands while retaining their existing font, size, color, and transform operators.
    /// </summary>
    /// <remarks>
    /// A replacement cannot cross separate PDF text operands. Fonts that use custom glyph encodings or omit replacement glyphs may not be safely replaceable;
    /// use the returned replacement results to determine what was changed.
    /// </remarks>
    /// <param name="source">A readable, seekable PDF stream. The caller retains ownership.</param>
    /// <param name="destination">The writable destination stream. The caller retains ownership.</param>
    /// <param name="replacements">The replacements to apply in order.</param>
    /// <param name="options">Optional matching and final-output settings.</param>
    /// <param name="cancellationToken">Token used to cancel processing.</param>
    /// <returns>Document and per-replacement results.</returns>
    ValueTask<PdfProcessResult> ReplaceText(Stream source, Stream destination, IReadOnlyCollection<PdfTextReplacement> replacements,
        PdfReplaceOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Replaces text in a PDF file and atomically replaces the destination after processing succeeds.</summary>
    /// <param name="sourcePath">The source PDF path.</param>
    /// <param name="destinationPath">The PDF file to create or replace.</param>
    /// <param name="replacements">The replacements to apply in order.</param>
    /// <param name="options">Optional matching and final-output settings.</param>
    /// <param name="cancellationToken">Token used to cancel processing.</param>
    /// <returns>Document and per-replacement results.</returns>
    ValueTask<PdfProcessResult> ReplaceTextFile(string sourcePath, string destinationPath, IReadOnlyCollection<PdfTextReplacement> replacements,
        PdfReplaceOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Rewrites a PDF using the requested cleanup, sanitization, and compression settings.</summary>
    /// <param name="source">A readable, seekable PDF stream. The caller retains ownership.</param>
    /// <param name="destination">The writable destination stream. The caller retains ownership.</param>
    /// <param name="options">Optional final-output settings.</param>
    /// <param name="cancellationToken">Token used to cancel processing.</param>
    /// <returns>Details about the produced document.</returns>
    ValueTask<PdfProcessResult> Optimize(Stream source, Stream destination, PdfOutputOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Rewrites a PDF file and atomically replaces the destination using the requested output settings.</summary>
    /// <param name="sourcePath">The source PDF path.</param>
    /// <param name="destinationPath">The PDF file to create or replace.</param>
    /// <param name="options">Optional final-output settings.</param>
    /// <param name="cancellationToken">Token used to cancel processing.</param>
    /// <returns>Details about the produced document.</returns>
    ValueTask<PdfProcessResult> OptimizeFile(string sourcePath, string destinationPath, PdfOutputOptions? options = null,
        CancellationToken cancellationToken = default);
}
