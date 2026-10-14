using Soenneker.Utils.MemoryStream.Abstract;
using System.IO.Compression;

namespace Soenneker.Pdfs.Processor.Internal;

internal static class PdfDocumentReader
{
    private static readonly byte[] ObjKeyword = "obj"u8.ToArray();
    private static readonly byte[] EndStreamKeyword = "endstream"u8.ToArray();

    internal static async ValueTask<PdfDocumentModel> Read(Stream stream, IMemoryStreamUtil memoryStreamUtil, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        if (stream.Length > PdfProcessingLimits.MaximumInputBytes)
            throw new PdfLimitExceededException("The PDF exceeds the input byte limit.");
        using MemoryStream input = memoryStreamUtil.GetSync();
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (input.Length + bytesRead > PdfProcessingLimits.MaximumInputBytes)
                throw new PdfLimitExceededException("The PDF exceeds the input byte limit.");
            input.Write(buffer, 0, bytesRead);
        }
        byte[] data = input.ToArray();

        if (data.Length < 8 || data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf("%PDF-"u8) < 0)
            throw new InvalidDataException("The source is not a PDF document.");

        var document = new PdfDocumentModel { DecodeBudget = new PdfDecodeBudget(cancellationToken) };
        var position = 0;
        while (TryFindObjectHeader(data, position, out int headerStart, out int number, out int generation, out int valueStart))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Objects.Count >= PdfProcessingLimits.MaximumObjects)
                throw new PdfLimitExceededException("The PDF exceeds the object limit.");
            if (number == 0 || generation > 65535)
                throw new InvalidDataException("Invalid PDF object identifier.");
            var reader = new PdfSyntaxReader(data, valueStart);
            PdfValue value;
            try
            {
                value = reader.ReadValue();
            }
            catch (InvalidDataException)
            {
                position = headerStart + 1;
                continue;
            }

            byte[]? streamData = null;
            reader.SkipWhiteSpaceAndComments();
            if (value is PdfDictionary dictionary && reader.TryReadKeyword("stream"u8))
            {
                reader.ConsumeStreamLineEnding();
                int streamStart = reader.Position;
                int? length = dictionary.Get("Length") is PdfNumber directLength ? directLength.AsInt32() : null;
                int streamEnd = length is >= 0 && length.Value <= data.Length - streamStart
                    ? streamStart + length.Value
                    : IndexOf(data, EndStreamKeyword, streamStart);

                if (streamEnd < streamStart)
                    throw new InvalidDataException($"PDF stream object {number} has no endstream marker.");

                streamData = data.AsSpan(streamStart, streamEnd - streamStart).ToArray();
                reader.Position = streamEnd;
                reader.TryReadKeyword("endstream"u8);
            }

            document.Objects[number] = new PdfIndirectObject
            {
                Number = number,
                Generation = generation,
                Value = value,
                StreamData = streamData
            };
            position = Math.Max(reader.Position, valueStart + 1);
        }

        ReadTrailer(data, document);
        ExpandObjectStreams(document, memoryStreamUtil);
        ResolveDocumentReferences(document);
        return document;
    }

    internal static byte[] DecodeStream(PdfIndirectObject streamObject, PdfDocumentModel document, IMemoryStreamUtil memoryStreamUtil)
    {
        if (streamObject.StreamData == null)
            throw new InvalidDataException($"PDF object {streamObject.Number} is not a stream.");
        if (streamObject.Value is not PdfDictionary dictionary)
            throw new InvalidDataException($"PDF stream object {streamObject.Number} has no dictionary.");

        PdfValue? filterValue = document.Resolve(dictionary.Get("Filter"));
        if (filterValue == null)
        {
            document.DecodeBudget.Charge(0, streamObject.StreamData.Length);
            return streamObject.StreamData.ToArray();
        }

        var filters = new List<string>();
        if (filterValue is PdfName name)
            filters.Add(name.Value);
        else if (filterValue is PdfArray array)
        {
            foreach (PdfValue item in array.Items)
            {
                if (document.Resolve(item) is PdfName itemName)
                    filters.Add(itemName.Value);
            }
        }

        if (filters.Count > PdfProcessingLimits.MaximumFilters)
            throw new PdfLimitExceededException("The PDF stream exceeds the filter limit.");
        if (filters.Count == 0)
            document.DecodeBudget.Charge(0, streamObject.StreamData.Length);
        byte[] result = streamObject.StreamData;
        foreach (string filter in filters)
        {
            result = filter switch
            {
                "FlateDecode" or "Fl" => Inflate(result, memoryStreamUtil, document.DecodeBudget),
                "ASCIIHexDecode" or "AHx" => DecodeAsciiHex(result, memoryStreamUtil, document.DecodeBudget),
                "ASCII85Decode" or "A85" => DecodeAscii85(result, memoryStreamUtil, document.DecodeBudget),
                "RunLengthDecode" or "RL" => DecodeRunLength(result, memoryStreamUtil, document.DecodeBudget),
                _ => throw new NotSupportedException($"The PDF stream filter '{filter}' is not supported.")
            };
        }

        return result;
    }

    private static byte[] Inflate(byte[] value, IMemoryStreamUtil memoryStreamUtil, PdfDecodeBudget budget)
    {
        using MemoryStream input = memoryStreamUtil.GetSync(value);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new PdfDecodedStream(memoryStreamUtil.GetSync(), budget);
        inflater.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] DecodeAsciiHex(byte[] value, IMemoryStreamUtil memoryStreamUtil, PdfDecodeBudget budget)
    {
        using var output = new PdfDecodedStream(memoryStreamUtil.GetSync(), budget);
        int high = -1;
        foreach (byte character in value)
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            if (character == (byte)'>')
                break;
            if (TryHex(character, out int digit))
            {
                if (high < 0)
                    high = digit;
                else
                {
                    output.WriteByte((byte)(high * 16 + digit));
                    high = -1;
                }
            }
            else if (!IsWhiteSpace(character))
                throw new InvalidDataException("An ASCIIHex PDF stream contains an invalid character.");
        }
        if (high >= 0)
            output.WriteByte((byte)(high * 16));
        return output.ToArray();
    }

    private static byte[] DecodeAscii85(byte[] value, IMemoryStreamUtil memoryStreamUtil, PdfDecodeBudget budget)
    {
        using var output = new PdfDecodedStream(memoryStreamUtil.GetSync(), budget);
        Span<byte> group = stackalloc byte[5];
        var groupCount = 0;
        var index = 0;
        while (index < value.Length)
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            byte character = value[index++];
            if (IsWhiteSpace(character))
                continue;
            if (character == (byte)'<' && index < value.Length && value[index] == (byte)'~')
            {
                index++;
                continue;
            }
            if (character == (byte)'~')
                break;
            if (character == (byte)'z')
            {
                if (groupCount != 0)
                    throw new InvalidDataException("An ASCII85 z marker occurred inside a partial group.");
                output.Write([0, 0, 0, 0]);
                continue;
            }
            if (character is < (byte)'!' or > (byte)'u')
                throw new InvalidDataException("An ASCII85 PDF stream contains an invalid character.");
            group[groupCount++] = character;
            if (groupCount == 5)
            {
                WriteAscii85Group(output, group, 4);
                groupCount = 0;
            }
        }

        if (groupCount == 1)
            throw new InvalidDataException("An ASCII85 PDF stream ends with an invalid partial group.");
        if (groupCount > 1)
        {
            int bytesToWrite = groupCount - 1;
            while (groupCount < 5)
                group[groupCount++] = (byte)'u';
            WriteAscii85Group(output, group, bytesToWrite);
        }
        return output.ToArray();
    }

    private static void WriteAscii85Group(Stream output, ReadOnlySpan<byte> group, int bytesToWrite)
    {
        ulong value = 0;
        foreach (byte character in group)
            value = value * 85 + (uint)(character - (byte)'!');
        if (value > uint.MaxValue)
            throw new InvalidDataException("An ASCII85 PDF stream group exceeds 32 bits.");
        Span<byte> decoded = stackalloc byte[4];
        decoded[0] = (byte)(value >> 24);
        decoded[1] = (byte)(value >> 16);
        decoded[2] = (byte)(value >> 8);
        decoded[3] = (byte)value;
        output.Write(decoded[..bytesToWrite]);
    }

    private static byte[] DecodeRunLength(byte[] value, IMemoryStreamUtil memoryStreamUtil, PdfDecodeBudget budget)
    {
        using var output = new PdfDecodedStream(memoryStreamUtil.GetSync(), budget);
        var position = 0;
        while (position < value.Length)
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            int length = value[position++];
            if (length == 128)
                break;
            if (length <= 127)
            {
                int count = length + 1;
                if (position + count > value.Length)
                    throw new InvalidDataException("A RunLength PDF stream ends inside a literal run.");
                output.Write(value, position, count);
                position += count;
            }
            else
            {
                if (position >= value.Length)
                    throw new InvalidDataException("A RunLength PDF stream ends before a repeated byte.");
                int count = 257 - length;
                byte repeated = value[position++];
                for (var index = 0; index < count; index++)
                    output.WriteByte(repeated);
            }
        }
        return output.ToArray();
    }

    private static void ReadTrailer(byte[] data, PdfDocumentModel document)
    {
        PdfDictionary? trailer = null;
        var search = 0;
        while ((search = IndexOf(data, "trailer"u8, search)) >= 0)
        {
            var reader = new PdfSyntaxReader(data, search + 7);
            reader.SkipWhiteSpaceAndComments();
            try
            {
                if (reader.ReadValue() is PdfDictionary candidate)
                    trailer = candidate;
            }
            catch (InvalidDataException)
            {
            }

            search += 7;
        }

        document.RootReference = trailer?.Get("Root") as PdfReference;
        document.InfoReference = trailer?.Get("Info") as PdfReference;
        document.EncryptReference = trailer?.Get("Encrypt") as PdfReference;
    }

    private static void ExpandObjectStreams(PdfDocumentModel document, IMemoryStreamUtil memoryStreamUtil)
    {
        PdfIndirectObject[] objectStreams = document.Objects.Values
            .Where(value => value.StreamData != null && value.Value is PdfDictionary dictionary && dictionary.GetName("Type", document) == "ObjStm")
            .ToArray();

        foreach (PdfIndirectObject objectStream in objectStreams)
        {
            var dictionary = (PdfDictionary)objectStream.Value;
            int count = dictionary.GetInteger("N", document)
                        ?? throw new InvalidDataException("A PDF object stream has no N value.");
            int first = dictionary.GetInteger("First", document)
                        ?? throw new InvalidDataException("A PDF object stream has no First value.");
            byte[] decoded = DecodeStream(objectStream, document, memoryStreamUtil);
            if (count < 0 || count > PdfProcessingLimits.MaximumObjects - document.Objects.Count ||
                first < 0 || first > decoded.Length || count > first / 4)
                throw new InvalidDataException("Invalid PDF object-stream count or header length.");
            var headerReader = new PdfSyntaxReader(decoded, 0);
            var entries = new (int Number, int Offset)[count];
            for (var index = 0; index < count; index++)
            {
                entries[index] = (headerReader.ReadInteger(), headerReader.ReadInteger());
            }

            if (headerReader.Position > first)
                throw new InvalidDataException("The PDF object-stream header exceeds First.");

            for (var index = 0; index < count; index++)
            {
                document.DecodeBudget.CancellationToken.ThrowIfCancellationRequested();
                if (entries[index].Number <= 0 || entries[index].Offset < 0 || entries[index].Offset >= decoded.Length - first)
                    throw new InvalidDataException("Invalid PDF object-stream entry.");
                var valueReader = new PdfSyntaxReader(decoded, first + entries[index].Offset);
                document.Objects.TryAdd(entries[index].Number, new PdfIndirectObject
                {
                    Number = entries[index].Number,
                    Value = valueReader.ReadValue()
                });
            }
        }
    }

    private static void ResolveDocumentReferences(PdfDocumentModel document)
    {
        PdfDictionary? xref = document.Objects.Values
            .Select(static value => value.Value)
            .OfType<PdfDictionary>()
            .LastOrDefault(dictionary => dictionary.GetName("Type", document) == "XRef");
        document.RootReference ??= xref?.Get("Root") as PdfReference;
        document.InfoReference ??= xref?.Get("Info") as PdfReference;
        document.EncryptReference ??= xref?.Get("Encrypt") as PdfReference;

        if (document.EncryptReference != null)
            throw new NotSupportedException("Encrypted PDF documents are not supported.");

        if (document.RootReference == null)
        {
            PdfIndirectObject? catalog = document.Objects.Values.FirstOrDefault(value =>
                value.Value is PdfDictionary dictionary && dictionary.GetName("Type", document) == "Catalog");
            if (catalog != null)
                document.RootReference = new PdfReference(catalog.Number, catalog.Generation);
        }

        if (document.RootReference == null)
            throw new InvalidDataException("The PDF catalog was not found.");
    }

    private static bool TryFindObjectHeader(byte[] data, int start, out int headerStart, out int number, out int generation, out int valueStart)
    {
        for (var index = Math.Max(0, start); index < data.Length; index++)
        {
            if (!IsDigit(data[index]) || index > 0 && !IsWhiteSpace(data[index - 1]))
                continue;

            int cursor = index;
            if (!TryReadUnsignedInteger(data, ref cursor, out number))
                continue;
            if (!SkipRequiredWhiteSpace(data, ref cursor) || !TryReadUnsignedInteger(data, ref cursor, out generation))
                continue;
            if (!SkipRequiredWhiteSpace(data, ref cursor) || !MatchesToken(data, cursor, ObjKeyword))
                continue;

            headerStart = index;
            valueStart = cursor + ObjKeyword.Length;
            return true;
        }

        headerStart = number = generation = valueStart = 0;
        return false;
    }

    private static bool TryReadUnsignedInteger(byte[] data, ref int position, out int value)
    {
        int start = position;
        long result = 0;
        while (position < data.Length && IsDigit(data[position]))
        {
            result = result * 10 + data[position++] - (byte)'0';
            if (result > int.MaxValue)
            {
                value = 0;
                return false;
            }
        }

        value = (int)result;
        return position > start;
    }

    private static bool SkipRequiredWhiteSpace(byte[] data, ref int position)
    {
        int start = position;
        while (position < data.Length && IsWhiteSpace(data[position]))
            position++;
        return position > start;
    }

    private static bool MatchesToken(byte[] data, int position, ReadOnlySpan<byte> token) =>
        position >= 0 && position + token.Length <= data.Length && data.AsSpan(position, token.Length).SequenceEqual(token) &&
        (position + token.Length == data.Length || PdfSyntaxReader.IsDelimiterOrWhiteSpace(data[position + token.Length]));

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> value, int start)
    {
        int relative = data.AsSpan(Math.Max(0, start)).IndexOf(value);
        return relative < 0 ? -1 : Math.Max(0, start) + relative;
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static bool IsWhiteSpace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool TryHex(byte value, out int digit)
    {
        if (value is >= (byte)'0' and <= (byte)'9') digit = value - (byte)'0';
        else if (value is >= (byte)'A' and <= (byte)'F') digit = value - (byte)'A' + 10;
        else if (value is >= (byte)'a' and <= (byte)'f') digit = value - (byte)'a' + 10;
        else { digit = 0; return false; }
        return true;
    }
}
