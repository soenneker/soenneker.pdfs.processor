using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using Soenneker.Pdfs.Processor.Abstract;
using Soenneker.Pdfs.Processor.Enums;
using Soenneker.Pdfs.Processor.Internal;
using Soenneker.Pdfs.Processor.Models;
using Soenneker.Pdfs.Processor.Options;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Path.Abstract;
using System.Text;

namespace Soenneker.Pdfs.Processor;

public sealed class PdfProcessor : IPdfProcessor
{
    private readonly IFileUtil _fileUtil;
    private readonly IPathUtil _pathUtil;

    public PdfProcessor(IFileUtil fileUtil, IPathUtil pathUtil)
    {
        _fileUtil = fileUtil;
        _pathUtil = pathUtil;
    }

    public async ValueTask<PdfProcessResult> Merge(IReadOnlyCollection<PdfMergeSource> sources, Stream destination, PdfMergeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ValidateDestination(destination);

        if (sources.Count == 0)
            throw new ArgumentException("At least one PDF source is required.", nameof(sources));

        options ??= new PdfMergeOptions();
        long? inputLength = SumLengths(sources.Select(static source => source.Stream));

        using var resultDocument = new PdfDocument();
        var sourceIndex = 0;

        foreach (PdfMergeSource source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSource(source.Stream, $"{nameof(sources)}[{sourceIndex}].{nameof(source.Stream)}");
            source.Stream.Position = 0;

            using PdfDocument input = PdfReader.Open(source.Stream, PdfDocumentOpenMode.Import);
            ValidatePageRange(source.StartPage, source.EndPage, input.PageCount, $"{nameof(sources)}[{sourceIndex}]");

            if (sourceIndex == 0 && options.PreserveFirstDocumentMetadata)
                CopyMetadata(input, resultDocument);

            int first = (source.StartPage ?? 1) - 1;
            int last = (source.EndPage ?? input.PageCount) - 1;

            for (int pageIndex = first; pageIndex <= last; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                resultDocument.AddPage(input.Pages[pageIndex]);
            }

            sourceIndex++;
        }

        int pageCount = resultDocument.PageCount;
        ApplyOutputOptions(resultDocument, options.Output);
        await Save(resultDocument, destination, cancellationToken);

        return CreateResult(pageCount, inputLength, destination);
    }

    public async ValueTask<PdfProcessResult> MergeFiles(IReadOnlyCollection<string> sourcePaths, string destinationPath,
        PdfMergeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        var streams = new List<FileStream>(sourcePaths.Count);
        try
        {
            var sources = new List<PdfMergeSource>(sourcePaths.Count);
            foreach (string sourcePath in sourcePaths)
            {
                FileStream stream = _fileUtil.OpenRead(sourcePath, log: false);
                streams.Add(stream);
                sources.Add(new PdfMergeSource { Stream = stream });
            }

            return await WriteAtomically(destinationPath, stream => Merge(sources, stream, options, cancellationToken), cancellationToken);
        }
        finally
        {
            foreach (FileStream stream in streams)
                await stream.DisposeAsync();
        }
    }

    public async ValueTask<PdfProcessResult> ReplaceText(Stream source, Stream destination,
        IReadOnlyCollection<PdfTextReplacement> replacements, PdfReplaceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(source, nameof(source));
        ValidateDestination(destination);
        ArgumentNullException.ThrowIfNull(replacements);

        if (replacements.Count == 0)
            throw new ArgumentException("At least one text replacement is required.", nameof(replacements));

        options ??= new PdfReplaceOptions();
        ReplacementState[] states = CreateReplacementStates(replacements, options);
        StringComparison comparison = options.Comparison == PdfTextComparison.OrdinalIgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        long? inputLength = TryGetLength(source);
        source.Position = 0;

        using PdfDocument document = PdfReader.Open(source, PdfDocumentOpenMode.Modify);

        for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int pageNumber = pageIndex + 1;
            PdfPage page = document.Pages[pageIndex];
            CSequence content = ContentReader.ReadContent(page);
            bool changed = ReplaceInContent(content, states, pageNumber, comparison, options.MatchAcrossTextFragments,
                options.MatchAcrossTextOperators);

            if (changed)
                page.Contents.ReplaceContent(content);
        }

        List<PdfTextReplacementResult> replacementResults = CreateReplacementResults(states);
        if (options.RequireAll)
        {
            PdfTextReplacementResult? missing = replacementResults.FirstOrDefault(static result => result.ReplacementCount == 0);
            if (missing != null)
                throw new InvalidOperationException($"The required PDF text '{missing.Search}' was not replaced: {missing.Message ?? missing.Status.ToString()}.");
        }

        int pageCount = document.PageCount;
        ApplyOutputOptions(document, options.Output);
        await Save(document, destination, cancellationToken);

        return CreateResult(pageCount, inputLength, destination, replacementResults);
    }

    public async ValueTask<PdfProcessResult> ReplaceTextFile(string sourcePath, string destinationPath,
        IReadOnlyCollection<PdfTextReplacement> replacements, PdfReplaceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using FileStream source = _fileUtil.OpenRead(sourcePath, log: false);
        return await WriteAtomically(destinationPath, stream => ReplaceText(source, stream, replacements, options, cancellationToken), cancellationToken);
    }

    public async ValueTask<PdfProcessResult> Optimize(Stream source, Stream destination, PdfOutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(source, nameof(source));
        ValidateDestination(destination);
        options ??= new PdfOutputOptions();
        long? inputLength = TryGetLength(source);
        source.Position = 0;

        using PdfDocument document = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
        cancellationToken.ThrowIfCancellationRequested();
        int pageCount = document.PageCount;
        ApplyOutputOptions(document, options);
        await Save(document, destination, cancellationToken);

        return CreateResult(pageCount, inputLength, destination);
    }

    public async ValueTask<PdfProcessResult> OptimizeFile(string sourcePath, string destinationPath, PdfOutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using FileStream source = _fileUtil.OpenRead(sourcePath, log: false);
        return await WriteAtomically(destinationPath, stream => Optimize(source, stream, options, cancellationToken), cancellationToken);
    }

    private static ReplacementState[] CreateReplacementStates(IReadOnlyCollection<PdfTextReplacement> replacements, PdfReplaceOptions options)
    {
        var result = new ReplacementState[replacements.Count];
        var index = 0;

        foreach (PdfTextReplacement replacement in replacements)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            if (string.IsNullOrEmpty(replacement.Search))
                throw new ArgumentException("PDF replacement search text cannot be null or empty.", nameof(replacements));
            if (replacement.MaximumReplacements is <= 0)
                throw new ArgumentOutOfRangeException(nameof(replacements), "Maximum replacements must be greater than zero.");
            if (replacement.StartPage is <= 0 || replacement.EndPage is <= 0 || replacement.StartPage > replacement.EndPage)
                throw new ArgumentOutOfRangeException(nameof(replacements), "Replacement page ranges must be positive and ordered.");

            if (replacement.Replacement == null)
                throw new ArgumentException("PDF replacement text cannot be null.", nameof(replacements));

            bool unsupported = options.RequireSingleByteReplacement && replacement.Replacement.Any(static character => character > byte.MaxValue);
            result[index++] = new ReplacementState(replacement, unsupported);
        }

        return result;
    }

    private static bool ReplaceInContent(CSequence sequence, ReplacementState[] states, int pageNumber, StringComparison comparison,
        bool matchAcrossTextFragments, bool matchAcrossTextOperators)
    {
        var changed = false;

        for (var operationIndex = 0; operationIndex < sequence.Count; operationIndex++)
        {
            CObject item = sequence[operationIndex];
            if (item is not COperator operation || operation.Name is not ("Tj" or "TJ" or "'" or "\""))
                continue;

            var fragments = new List<CString>();
            CollectStrings(operation.Operands, fragments);

            if (matchAcrossTextOperators && operation.Name is "Tj" or "TJ")
            {
                while (operationIndex + 1 < sequence.Count &&
                       sequence[operationIndex + 1] is COperator nextOperation &&
                       nextOperation.Name is "Tj" or "TJ")
                {
                    CollectStrings(nextOperation.Operands, fragments);
                    operationIndex++;
                }
            }

            if (matchAcrossTextFragments)
            {
                changed |= ReplaceInFragments(fragments, states, pageNumber, comparison);
            }
            else
            {
                foreach (CString fragment in fragments)
                    changed |= ReplaceInFragments([fragment], states, pageNumber, comparison);
            }
        }

        return changed;
    }

    private static void CollectStrings(CSequence sequence, List<CString> fragments)
    {
        foreach (CObject operand in sequence)
        {
            if (operand is CString text)
                fragments.Add(text);
            else if (operand is CSequence nested)
                CollectStrings(nested, fragments);
        }
    }

    private static bool ReplaceInFragments(List<CString> fragments, ReplacementState[] states, int pageNumber, StringComparison comparison)
    {
        if (fragments.Count == 0)
            return false;

        var changed = false;
        foreach (ReplacementState state in states)
        {
            if (!state.IsEligible(pageNumber))
                continue;

            int remaining = state.Replacement.MaximumReplacements is int maximum ? maximum - state.Count : int.MaxValue;
            if (remaining <= 0)
                continue;

            int replaced = ReplaceAcrossFragments(fragments, state.Replacement.Search, state.Replacement.Replacement, comparison, remaining,
                out int crossFragmentCount);
            if (replaced == 0)
                continue;

            state.Count += replaced;
            state.CrossFragmentCount += crossFragmentCount;
            state.Pages.Add(pageNumber);
            changed = true;
        }

        return changed;
    }

    private static int ReplaceAcrossFragments(List<CString> fragments, string search, string replacement, StringComparison comparison, int maximum,
        out int crossFragmentCount)
    {
        crossFragmentCount = 0;
        string logicalText = Concatenate(fragments);
        var matches = new List<int>();
        var searchFrom = 0;

        while (matches.Count < maximum)
        {
            int match = logicalText.IndexOf(search, searchFrom, comparison);
            if (match < 0)
                break;

            matches.Add(match);
            searchFrom = match + search.Length;
        }

        for (int matchIndex = matches.Count - 1; matchIndex >= 0; matchIndex--)
        {
            int match = matches[matchIndex];
            FragmentPosition start = Locate(fragments, match);
            FragmentPosition end = Locate(fragments, match + search.Length - 1);
            CString startFragment = fragments[start.Index];

            if (start.Index == end.Index)
            {
                string value = startFragment.Value;
                startFragment.Value = string.Concat(value.AsSpan(0, start.Offset), replacement,
                    value.AsSpan(end.Offset + 1));
            }
            else
            {
                string prefix = startFragment.Value[..start.Offset];
                string suffix = fragments[end.Index].Value[(end.Offset + 1)..];
                startFragment.Value = prefix + replacement;

                for (int index = start.Index + 1; index < end.Index; index++)
                    fragments[index].Value = string.Empty;

                fragments[end.Index].Value = suffix;
                crossFragmentCount++;
            }
        }

        return matches.Count;
    }

    private static string Concatenate(List<CString> fragments)
    {
        var builder = new StringBuilder();
        foreach (CString fragment in fragments)
            builder.Append(fragment.Value);
        return builder.ToString();
    }

    private static FragmentPosition Locate(List<CString> fragments, int characterIndex)
    {
        var offset = 0;
        for (var index = 0; index < fragments.Count; index++)
        {
            int length = fragments[index].Value.Length;
            if (characterIndex < offset + length)
                return new FragmentPosition(index, characterIndex - offset);
            offset += length;
        }

        throw new InvalidOperationException("The PDF text-fragment position could not be resolved.");
    }

    private static List<PdfTextReplacementResult> CreateReplacementResults(ReplacementState[] states)
    {
        var results = new List<PdfTextReplacementResult>(states.Length);
        foreach (ReplacementState state in states)
        {
            PdfReplacementStatus status;
            string? message = null;

            if (state.Unsupported)
            {
                status = PdfReplacementStatus.UnsupportedReplacement;
                message = "The replacement contains characters outside the supported single-byte content-string range.";
            }
            else if (state.Count == 0)
            {
                status = PdfReplacementStatus.NotFound;
                message = "No match occurred within one supported PDF text operand.";
            }
            else if (state.Replacement.MaximumReplacements is int maximum && state.Count >= maximum)
            {
                status = PdfReplacementStatus.MatchLimitReached;
            }
            else
            {
                status = PdfReplacementStatus.Replaced;
            }

            results.Add(new PdfTextReplacementResult
            {
                Search = state.Replacement.Search,
                Replacement = state.Replacement.Replacement,
                Status = status,
                ReplacementCount = state.Count,
                CrossFragmentReplacementCount = state.CrossFragmentCount,
                PageNumbers = state.Pages.Order().ToArray(),
                FontResourcePreserved = state.Count > 0,
                Message = message
            });
        }

        return results;
    }

    private static void ApplyOutputOptions(PdfDocument document, PdfOutputOptions options)
    {
        PdfCompressionProfile compression = options.Compression ?? PdfCompressionProfile.Balanced;
        document.Options.NoCompression = compression == PdfCompressionProfile.None;
        document.Options.CompressContentStreams = compression != PdfCompressionProfile.None;
        document.Options.FlateEncodeMode = compression.Value switch
        {
            PdfCompressionProfile.FastValue => PdfFlateEncodeMode.BestSpeed,
            PdfCompressionProfile.MaximumValue => PdfFlateEncodeMode.BestCompression,
            _ => PdfFlateEncodeMode.Default
        };
        document.Options.EnableCcittCompressionForBilevelImages = compression == PdfCompressionProfile.Maximum;

        if (options.RemoveMetadata)
            document.Info.Elements.Clear();

        PdfDictionary? names = document.Internals.Catalog.Elements.GetDictionary("/Names");
        if (options.RemoveEmbeddedFiles)
            names?.Elements.Remove("/EmbeddedFiles");

        if (options.RemoveJavaScriptAndActions)
        {
            names?.Elements.Remove("/JavaScript");
            document.Internals.Catalog.Elements.Remove("/OpenAction");
            document.Internals.Catalog.Elements.Remove("/AA");
        }

        foreach (PdfPage page in document.Pages)
        {
            if (options.RemoveAnnotations)
                page.Annotations.Clear();
            if (options.RemoveJavaScriptAndActions)
                page.Elements.Remove("/AA");
        }
    }

    private static void CopyMetadata(PdfDocument source, PdfDocument destination)
    {
        destination.Info.Title = source.Info.Title;
        destination.Info.Author = source.Info.Author;
        destination.Info.Subject = source.Info.Subject;
        destination.Info.Keywords = source.Info.Keywords;
        destination.Info.Creator = source.Info.Creator;
        destination.Info.CreationDate = source.Info.CreationDate;
        destination.Info.ModificationDate = source.Info.ModificationDate;
    }

    private static async ValueTask Save(PdfDocument document, Stream destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await document.SaveAsync(destination, false);
        await destination.FlushAsync(cancellationToken);
    }

    private async ValueTask<PdfProcessResult> WriteAtomically(string destinationPath,
        Func<Stream, ValueTask<PdfProcessResult>> write, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string fullPath = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullPath)!;
        await _fileUtil.CreateDirectory(directory, cancellationToken);
        string temporaryPath = await _pathUtil.GetRandomUniqueFilePath(directory, ".tmp", cancellationToken);

        try
        {
            PdfProcessResult result;
            await using (FileStream destination = _fileUtil.OpenWrite(temporaryPath, log: false))
            {
                result = await write(destination);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _fileUtil.Move(temporaryPath, fullPath, log: false, cancellationToken);
            return result;
        }
        finally
        {
            await _fileUtil.TryDeleteIfExists(temporaryPath, log: false);
        }
    }

    private static void ValidateSource(Stream stream, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(stream, parameterName);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("PDF source streams must be readable and seekable.", parameterName);
    }

    private static void ValidateDestination(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
            throw new ArgumentException("The PDF destination stream must be writable.", nameof(stream));
    }

    private static void ValidatePageRange(int? startPage, int? endPage, int pageCount, string parameterName)
    {
        int start = startPage ?? 1;
        int end = endPage ?? pageCount;
        if (start < 1 || end < start || end > pageCount)
            throw new ArgumentOutOfRangeException(parameterName, $"The page range {start}-{end} is outside the document's 1-{pageCount} page range.");
    }

    private static long? SumLengths(IEnumerable<Stream> streams)
    {
        long total = 0;
        foreach (Stream stream in streams)
        {
            long? length = TryGetLength(stream);
            if (length == null)
                return null;
            total += length.Value;
        }
        return total;
    }

    private static long? TryGetLength(Stream stream)
    {
        try
        {
            return stream.CanSeek ? stream.Length : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static PdfProcessResult CreateResult(int pageCount, long? inputLength, Stream destination,
        IReadOnlyList<PdfTextReplacementResult>? replacements = null) => new()
        {
            PageCount = pageCount,
            InputLength = inputLength,
            OutputLength = TryGetLength(destination),
            Replacements = replacements ?? []
        };

}
