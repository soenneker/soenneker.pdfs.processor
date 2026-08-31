using Soenneker.Pdfs.Processor.Enums;
using Soenneker.Pdfs.Processor.Options;
using Soenneker.Utils.MemoryStream.Abstract;
using Soenneker.Utils.PooledStringBuilders;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Soenneker.Pdfs.Processor.Internal;

internal static class PdfDocumentWriter
{
    internal static async ValueTask Write(PdfDocumentModel document, Stream destination, IMemoryStreamUtil memoryStreamUtil,
        CancellationToken cancellationToken)
    {
        if (document.RootReference == null)
            throw new InvalidDataException("The PDF has no catalog reference.");

        if (!destination.CanSeek)
        {
            using MemoryStream buffer = await memoryStreamUtil.Get(cancellationToken);
            await Write(document, buffer, memoryStreamUtil, cancellationToken);
            buffer.Position = 0;
            await buffer.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            return;
        }

        PruneUnreachableObjects(document);

        var offsets = new Dictionary<int, long>();
        await WriteAscii(destination, "%PDF-1.7\n%\xE2\xE3\xCF\xD3\n", cancellationToken);

        foreach (PdfIndirectObject indirect in document.Objects.Values.OrderBy(value => value.Number))
        {
            cancellationToken.ThrowIfCancellationRequested();
            offsets[indirect.Number] = destination.Position;
            await WriteAscii(destination, $"{indirect.Number} {indirect.Generation} obj\n", cancellationToken);

            if (indirect.StreamData != null)
            {
                if (indirect.Value is not PdfDictionary dictionary)
                    throw new InvalidDataException($"PDF stream object {indirect.Number} has no dictionary.");
                dictionary.Items["Length"] = new PdfNumber(indirect.StreamData.Length, true);
                await WriteValue(destination, dictionary, cancellationToken);
                await WriteAscii(destination, "\nstream\n", cancellationToken);
                await destination.WriteAsync(indirect.StreamData, cancellationToken);
                await WriteAscii(destination, "\nendstream\n", cancellationToken);
            }
            else
            {
                await WriteValue(destination, indirect.Value, cancellationToken);
                await WriteAscii(destination, "\n", cancellationToken);
            }

            await WriteAscii(destination, "endobj\n", cancellationToken);
        }

        long xrefOffset = destination.Position;
        int size = document.Objects.Count == 0 ? 1 : document.Objects.Keys.Max() + 1;
        await WriteAscii(destination, $"xref\n0 {size}\n", cancellationToken);
        await WriteAscii(destination, "0000000000 65535 f \n", cancellationToken);
        for (var number = 1; number < size; number++)
        {
            if (offsets.TryGetValue(number, out long offset))
                await WriteAscii(destination, $"{offset:0000000000} {document.Objects[number].Generation:00000} n \n", cancellationToken);
            else
                await WriteAscii(destination, "0000000000 65535 f \n", cancellationToken);
        }

        await WriteAscii(destination, $"trailer\n<< /Size {size} /Root {document.RootReference.ObjectNumber} {document.RootReference.Generation} R", cancellationToken);
        if (document.InfoReference != null)
            await WriteAscii(destination, $" /Info {document.InfoReference.ObjectNumber} {document.InfoReference.Generation} R", cancellationToken);
        await WriteAscii(destination, $" >>\nstartxref\n{xrefOffset}\n%%EOF\n", cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    internal static void ApplyOutputOptions(PdfDocumentModel document, PdfOutputOptions options, IMemoryStreamUtil memoryStreamUtil)
    {
        if (options.RemoveMetadata)
            document.InfoReference = null;

        PdfDictionary catalog = document.Catalog;
        if (options.RemoveEmbeddedFiles || options.RemoveJavaScriptAndActions)
        {
            PdfDictionary? names = catalog.GetDictionary("Names", document);
            if (options.RemoveEmbeddedFiles)
                names?.Items.Remove("EmbeddedFiles");
            if (options.RemoveJavaScriptAndActions)
                names?.Items.Remove("JavaScript");
        }

        if (options.RemoveJavaScriptAndActions)
        {
            catalog.Items.Remove("OpenAction");
            catalog.Items.Remove("AA");
        }

        foreach (PdfReference pageReference in document.GetPages())
        {
            var page = (PdfDictionary)document.Resolve(pageReference)!;
            if (options.RemoveAnnotations)
                page.Items.Remove("Annots");
            if (options.RemoveJavaScriptAndActions)
                page.Items.Remove("AA");
        }

        RecompressStreams(document, options.Compression, memoryStreamUtil);
    }

    internal static void SetDecodedStream(PdfIndirectObject streamObject, byte[] decoded, PdfCompressionProfile profile,
        IMemoryStreamUtil memoryStreamUtil)
    {
        var dictionary = (PdfDictionary)streamObject.Value;
        dictionary.Items.Remove("DecodeParms");
        if (profile == PdfCompressionProfile.None)
        {
            dictionary.Items.Remove("Filter");
            streamObject.StreamData = decoded;
            return;
        }

        dictionary.Items["Filter"] = new PdfName("FlateDecode");
        streamObject.StreamData = Compress(decoded, GetCompressionLevel(profile), memoryStreamUtil);
    }

    private static void RecompressStreams(PdfDocumentModel document, PdfCompressionProfile profile, IMemoryStreamUtil memoryStreamUtil)
    {
        foreach (PdfIndirectObject streamObject in document.Objects.Values.Where(value => value.StreamData != null))
        {
            var dictionary = (PdfDictionary)streamObject.Value;
            if (dictionary.Get("DecodeParms") != null)
                continue;

            PdfValue? filter = document.Resolve(dictionary.Get("Filter"));
            if (filter is not null && filter is not PdfName { Value: "FlateDecode" or "Fl" })
                continue;

            byte[] decoded;
            try
            {
                decoded = PdfDocumentReader.DecodeStream(streamObject, document, memoryStreamUtil);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            SetDecodedStream(streamObject, decoded, profile, memoryStreamUtil);
        }
    }

    private static void PruneUnreachableObjects(PdfDocumentModel document)
    {
        var reachable = new HashSet<int>();
        Visit(document.RootReference!, document, reachable);
        if (document.InfoReference != null)
            Visit(document.InfoReference, document, reachable);

        foreach (int objectNumber in document.Objects.Keys.Where(number => !reachable.Contains(number)).ToArray())
            document.Objects.Remove(objectNumber);
    }

    private static void Visit(PdfValue value, PdfDocumentModel document, HashSet<int> reachable)
    {
        switch (value)
        {
            case PdfReference reference:
                if (!reachable.Add(reference.ObjectNumber) || !document.Objects.TryGetValue(reference.ObjectNumber, out PdfIndirectObject? indirect))
                    return;
                Visit(indirect.Value, document, reachable);
                break;
            case PdfArray array:
                foreach (PdfValue item in array.Items)
                    Visit(item, document, reachable);
                break;
            case PdfDictionary dictionary:
                foreach (PdfValue item in dictionary.Items.Values)
                    Visit(item, document, reachable);
                break;
        }
    }

    private static CompressionLevel GetCompressionLevel(PdfCompressionProfile profile)
    {
        if (profile == PdfCompressionProfile.Fast)
            return CompressionLevel.Fastest;
        if (profile == PdfCompressionProfile.Maximum)
            return CompressionLevel.SmallestSize;
        return CompressionLevel.Optimal;
    }

    private static byte[] Compress(byte[] value, CompressionLevel level, IMemoryStreamUtil memoryStreamUtil)
    {
        using MemoryStream output = memoryStreamUtil.GetSync();
        using (var compressor = new ZLibStream(output, level, leaveOpen: true))
            compressor.Write(value);
        return output.ToArray();
    }

    private static async ValueTask WriteValue(Stream destination, PdfValue value, CancellationToken cancellationToken)
    {
        switch (value)
        {
            case PdfNull:
                await WriteAscii(destination, "null", cancellationToken);
                break;
            case PdfBoolean boolean:
                await WriteAscii(destination, boolean.Value ? "true" : "false", cancellationToken);
                break;
            case PdfNumber number:
                await WriteAscii(destination, number.ToString(), cancellationToken);
                break;
            case PdfName name:
                await WriteAscii(destination, "/" + EscapeName(name.Value), cancellationToken);
                break;
            case PdfString text:
                await WriteString(destination, text, cancellationToken);
                break;
            case PdfReference reference:
                await WriteAscii(destination, $"{reference.ObjectNumber} {reference.Generation} R", cancellationToken);
                break;
            case PdfArray array:
                await WriteAscii(destination, "[", cancellationToken);
                for (var index = 0; index < array.Items.Count; index++)
                {
                    if (index > 0) await WriteAscii(destination, " ", cancellationToken);
                    await WriteValue(destination, array.Items[index], cancellationToken);
                }
                await WriteAscii(destination, "]", cancellationToken);
                break;
            case PdfDictionary dictionary:
                await WriteAscii(destination, "<<", cancellationToken);
                foreach ((string key, PdfValue item) in dictionary.Items)
                {
                    await WriteAscii(destination, " /" + EscapeName(key) + " ", cancellationToken);
                    await WriteValue(destination, item, cancellationToken);
                }
                await WriteAscii(destination, " >>", cancellationToken);
                break;
            default:
                throw new InvalidDataException($"Unsupported PDF value type {value.GetType().Name}.");
        }
    }

    private static async ValueTask WriteString(Stream destination, PdfString value, CancellationToken cancellationToken)
    {
        if (value.Hex)
        {
            await WriteAscii(destination, "<" + Convert.ToHexString(value.Value) + ">", cancellationToken);
            return;
        }

        await WriteAscii(destination, "(", cancellationToken);
        foreach (byte character in value.Value)
        {
            string escaped = character switch
            {
                (byte)'(' => "\\(",
                (byte)')' => "\\)",
                (byte)'\\' => "\\\\",
                (byte)'\n' => "\\n",
                (byte)'\r' => "\\r",
                (byte)'\t' => "\\t",
                (byte)'\b' => "\\b",
                (byte)'\f' => "\\f",
                < 32 or > 126 => $"\\{Convert.ToString(character, 8)!.PadLeft(3, '0')}",
                _ => ((char)character).ToString(CultureInfo.InvariantCulture)
            };
            await WriteAscii(destination, escaped, cancellationToken);
        }
        await WriteAscii(destination, ")", cancellationToken);
    }

    private static string EscapeName(string value)
    {
        var builder = new PooledStringBuilder(value.Length);
        foreach (byte character in Encoding.Latin1.GetBytes(value))
        {
            if (character is < 33 or > 126 or (byte)'#' or (byte)'%' or (byte)'/' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or
                (byte)'[' or (byte)']' or (byte)'{' or (byte)'}')
            {
                builder.Append('#');
                builder.Append(character.ToString("X2", CultureInfo.InvariantCulture));
            }
            else
                builder.Append((char)character);
        }
        return builder.ToStringAndDispose();
    }

    private static ValueTask WriteAscii(Stream destination, string value, CancellationToken cancellationToken) =>
        destination.WriteAsync(Encoding.Latin1.GetBytes(value), cancellationToken);
}
