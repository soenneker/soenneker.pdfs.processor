using Soenneker.Pdfs.Processor.Abstract;
using Soenneker.Pdfs.Processor.Enums;
using Soenneker.Pdfs.Processor.Internal;
using Soenneker.Pdfs.Processor.Models;
using Soenneker.Pdfs.Processor.Options;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.MemoryStream.Abstract;
using Soenneker.Utils.Path.Abstract;
using System.Text;

namespace Soenneker.Pdfs.Processor;

/// <inheritdoc cref="IPdfProcessor" />
public sealed class PdfProcessor : IPdfProcessor
{
    private readonly IFileUtil _fileUtil;
    private readonly IMemoryStreamUtil _memoryStreamUtil;
    private readonly IPathUtil _pathUtil;

    public PdfProcessor(IFileUtil fileUtil, IMemoryStreamUtil memoryStreamUtil, IPathUtil pathUtil)
    {
        _fileUtil = fileUtil;
        _memoryStreamUtil = memoryStreamUtil;
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
        PdfDocumentModel resultDocument = PdfDocumentCloner.CreateDocument();
        var sourceIndex = 0;

        foreach (PdfMergeSource source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSource(source.Stream, $"{nameof(sources)}[{sourceIndex}].{nameof(source.Stream)}");
            PdfDocumentModel input = await PdfDocumentReader.Read(source.Stream, _memoryStreamUtil, cancellationToken);
            List<PdfReference> pages = input.GetPages();
            ValidatePageRange(source.StartPage, source.EndPage, pages.Count, $"{nameof(sources)}[{sourceIndex}]");
            var map = new Dictionary<int, PdfReference>();

            if (sourceIndex == 0 && options.PreserveFirstDocumentMetadata && input.InfoReference != null)
                resultDocument.InfoReference = PdfDocumentCloner.CloneReference(input, resultDocument, input.InfoReference, map);

            int first = (source.StartPage ?? 1) - 1;
            int last = (source.EndPage ?? pages.Count) - 1;
            for (int pageIndex = first; pageIndex <= last; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PdfDocumentCloner.AddPage(input, pages[pageIndex], resultDocument, map);
            }
            sourceIndex++;
        }

        int pageCount = resultDocument.GetPages().Count;
        PdfDocumentWriter.ApplyOutputOptions(resultDocument, options.Output, _memoryStreamUtil);
        await PdfDocumentWriter.Write(resultDocument, destination, _memoryStreamUtil, cancellationToken);
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
        PdfDocumentModel document = await PdfDocumentReader.Read(source, _memoryStreamUtil, cancellationToken);
        List<PdfReference> pages = document.GetPages();

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfDictionary page = (PdfDictionary)document.Resolve(pages[pageIndex])!;
            foreach (PdfIndirectObject contentStream in GetContentStreams(document, page))
            {
                byte[] decoded = PdfDocumentReader.DecodeStream(contentStream, document, _memoryStreamUtil);
                PdfContentDocument content = PdfContentDocument.Parse(decoded);
                if (ReplaceInContent(content, states, pageIndex + 1, comparison, options.MatchAcrossTextFragments, options.MatchAcrossTextOperators))
                    PdfDocumentWriter.SetDecodedStream(contentStream, content.ToBytes(_memoryStreamUtil), options.Output.Compression, _memoryStreamUtil);
            }
        }

        List<PdfTextReplacementResult> replacementResults = CreateReplacementResults(states);
        if (options.RequireAll)
        {
            PdfTextReplacementResult? missing = replacementResults.FirstOrDefault(static result => result.ReplacementCount == 0);
            if (missing != null)
                throw new InvalidOperationException($"The required PDF text '{missing.Search}' was not replaced: {missing.Message ?? missing.Status.ToString()}.");
        }

        PdfDocumentWriter.ApplyOutputOptions(document, options.Output, _memoryStreamUtil);
        await PdfDocumentWriter.Write(document, destination, _memoryStreamUtil, cancellationToken);
        return CreateResult(pages.Count, inputLength, destination, replacementResults);
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
        PdfDocumentModel document = await PdfDocumentReader.Read(source, _memoryStreamUtil, cancellationToken);
        int pageCount = document.GetPages().Count;
        cancellationToken.ThrowIfCancellationRequested();
        PdfDocumentWriter.ApplyOutputOptions(document, options, _memoryStreamUtil);
        await PdfDocumentWriter.Write(document, destination, _memoryStreamUtil, cancellationToken);
        return CreateResult(pageCount, inputLength, destination);
    }

    public async ValueTask<PdfProcessResult> OptimizeFile(string sourcePath, string destinationPath, PdfOutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using FileStream source = _fileUtil.OpenRead(sourcePath, log: false);
        return await WriteAtomically(destinationPath, stream => Optimize(source, stream, options, cancellationToken), cancellationToken);
    }

    private static List<PdfIndirectObject> GetContentStreams(PdfDocumentModel document, PdfDictionary page)
    {
        var result = new List<PdfIndirectObject>();
        CollectContentStreams(document, page.Get("Contents"), result, []);
        return result;
    }

    private static void CollectContentStreams(PdfDocumentModel document, PdfValue? value, List<PdfIndirectObject> result, HashSet<int> visited)
    {
        if (value is PdfReference reference)
        {
            if (!visited.Add(reference.ObjectNumber))
                return;
            PdfIndirectObject indirect = document.ResolveObject(reference);
            if (indirect.StreamData != null)
                result.Add(indirect);
            else
                CollectContentStreams(document, indirect.Value, result, visited);
            return;
        }

        if (value is PdfArray array)
            foreach (PdfValue item in array.Items)
                CollectContentStreams(document, item, result, visited);
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

    private static bool ReplaceInContent(PdfContentDocument content, ReplacementState[] states, int pageNumber, StringComparison comparison,
        bool matchAcrossTextFragments, bool matchAcrossTextOperators)
    {
        var changed = false;
        for (var operationIndex = 0; operationIndex < content.Entries.Count; operationIndex++)
        {
            if (content.Entries[operationIndex] is not PdfContentOperation operation || operation.Name is not ("Tj" or "TJ" or "'" or "\""))
                continue;

            var fragments = new List<PdfString>();
            CollectStrings(operation.Operands, fragments);
            if (matchAcrossTextOperators && operation.Name is "Tj" or "TJ")
            {
                while (operationIndex + 1 < content.Entries.Count &&
                       content.Entries[operationIndex + 1] is PdfContentOperation nextOperation && nextOperation.Name is "Tj" or "TJ")
                {
                    CollectStrings(nextOperation.Operands, fragments);
                    operationIndex++;
                }
            }

            if (matchAcrossTextFragments)
                changed |= ReplaceInFragments(fragments, states, pageNumber, comparison);
            else
                foreach (PdfString fragment in fragments)
                    changed |= ReplaceInFragments([fragment], states, pageNumber, comparison);
        }
        return changed;
    }

    private static void CollectStrings(IEnumerable<PdfValue> values, List<PdfString> fragments)
    {
        foreach (PdfValue value in values)
        {
            if (value is PdfString text)
                fragments.Add(text);
            else if (value is PdfArray array)
                CollectStrings(array.Items, fragments);
        }
    }

    private static bool ReplaceInFragments(List<PdfString> fragments, ReplacementState[] states, int pageNumber, StringComparison comparison)
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

    private static int ReplaceAcrossFragments(List<PdfString> fragments, string search, string replacement, StringComparison comparison, int maximum,
        out int crossFragmentCount)
    {
        crossFragmentCount = 0;
        string logicalText = string.Concat(fragments.Select(static fragment => Encoding.Latin1.GetString(fragment.Value)));
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

        if (matches.Count == 0)
            return 0;

        byte[] replacementBytes = Encoding.Latin1.GetBytes(replacement);
        for (int matchIndex = matches.Count - 1; matchIndex >= 0; matchIndex--)
        {
            int match = matches[matchIndex];
            FragmentPosition start = Locate(fragments, match);
            FragmentPosition end = Locate(fragments, match + search.Length - 1);
            PdfString startFragment = fragments[start.Index];
            if (start.Index == end.Index)
            {
                byte[] current = startFragment.Value;
                startFragment.Value = [.. current.AsSpan(0, start.Offset), .. replacementBytes, .. current.AsSpan(end.Offset + 1)];
            }
            else
            {
                byte[] suffix = fragments[end.Index].Value[(end.Offset + 1)..];
                startFragment.Value = [.. startFragment.Value.AsSpan(0, start.Offset), .. replacementBytes];
                for (int index = start.Index + 1; index < end.Index; index++)
                    fragments[index].Value = [];
                fragments[end.Index].Value = suffix;
                crossFragmentCount++;
            }
        }
        return matches.Count;
    }

    private static FragmentPosition Locate(List<PdfString> fragments, int characterIndex)
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
                message = "No match occurred within supported PDF text operands.";
            }
            else if (state.Replacement.MaximumReplacements is int maximum && state.Count >= maximum)
                status = PdfReplacementStatus.MatchLimitReached;
            else
                status = PdfReplacementStatus.Replaced;

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

    private async ValueTask<PdfProcessResult> WriteAtomically(string destinationPath, Func<Stream, ValueTask<PdfProcessResult>> write,
        CancellationToken cancellationToken)
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
                result = await write(destination);
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
