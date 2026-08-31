using Soenneker.Utils.MemoryStream.Abstract;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Soenneker.Pdfs.Processor.Internal;

internal static class PdfDocumentReader
{
    private static readonly byte[] ObjKeyword = "obj"u8.ToArray();
    private static readonly byte[] EndStreamKeyword = "endstream"u8.ToArray();

    internal static async ValueTask<PdfDocumentModel> Read(Stream stream, IMemoryStreamUtil memoryStreamUtil, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        byte[] data = await memoryStreamUtil.GetBytesFromStream(stream, keepOpen: true, cancellationToken);

        if (data.Length < 8 || data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf("%PDF-"u8) < 0)
            throw new InvalidDataException("The source is not a PDF document.");

        var document = new PdfDocumentModel();
        var position = 0;
        while (TryFindObjectHeader(data, position, out int headerStart, out int number, out int generation, out int valueStart))
        {
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
            if (value is PdfDictionary dictionary && reader.TryReadKeyword("stream"))
            {
                reader.ConsumeStreamLineEnding();
                int streamStart = reader.Position;
                int? length = dictionary.Get("Length") is PdfNumber directLength ? directLength.AsInt32() : null;
                int streamEnd = length is >= 0 && streamStart + length.Value <= data.Length
                    ? streamStart + length.Value
                    : IndexOf(data, EndStreamKeyword, streamStart);

                if (streamEnd < streamStart)
                    throw new InvalidDataException($"PDF stream object {number} has no endstream marker.");

                streamData = data.AsSpan(streamStart, streamEnd - streamStart).ToArray();
                reader.Position = streamEnd;
                reader.TryReadKeyword("endstream");
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
            return streamObject.StreamData.ToArray();

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

        byte[] result = streamObject.StreamData;
        foreach (string filter in filters)
        {
            result = filter switch
            {
                "FlateDecode" or "Fl" => Inflate(result, memoryStreamUtil),
                "ASCIIHexDecode" or "AHx" => DecodeAsciiHex(result),
                "ASCII85Decode" or "A85" => DecodeAscii85(result, memoryStreamUtil),
                "RunLengthDecode" or "RL" => DecodeRunLength(result, memoryStreamUtil),
                _ => throw new NotSupportedException($"The PDF stream filter '{filter}' is not supported.")
            };
        }

        return result;
    }

    private static byte[] Inflate(byte[] value, IMemoryStreamUtil memoryStreamUtil)
    {
        using MemoryStream input = memoryStreamUtil.GetSync(value);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress);
        using MemoryStream output = memoryStreamUtil.GetSync();
        inflater.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] DecodeAsciiHex(byte[] value)
    {
        var digits = new List<int>();
        foreach (byte character in value)
        {
            if (character == (byte)'>')
                break;
            if (TryHex(character, out int digit))
                digits.Add(digit);
            else if (!IsWhiteSpace(character))
                throw new InvalidDataException("An ASCIIHex PDF stream contains an invalid character.");
        }
        if (digits.Count % 2 != 0)
            digits.Add(0);
        var result = new byte[digits.Count / 2];
        for (var index = 0; index < result.Length; index++)
            result[index] = (byte)(digits[index * 2] * 16 + digits[index * 2 + 1]);
        return result;
    }

    private static byte[] DecodeAscii85(byte[] value, IMemoryStreamUtil memoryStreamUtil)
    {
        using MemoryStream output = memoryStreamUtil.GetSync();
        var group = new List<byte>(5);
        var index = 0;
        while (index < value.Length)
        {
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
                if (group.Count != 0)
                    throw new InvalidDataException("An ASCII85 z marker occurred inside a partial group.");
                output.Write([0, 0, 0, 0]);
                continue;
            }
            if (character is < (byte)'!' or > (byte)'u')
                throw new InvalidDataException("An ASCII85 PDF stream contains an invalid character.");
            group.Add(character);
            if (group.Count == 5)
            {
                WriteAscii85Group(output, group, 4);
                group.Clear();
            }
        }

        if (group.Count == 1)
            throw new InvalidDataException("An ASCII85 PDF stream ends with an invalid partial group.");
        if (group.Count > 1)
        {
            int bytesToWrite = group.Count - 1;
            while (group.Count < 5)
                group.Add((byte)'u');
            WriteAscii85Group(output, group, bytesToWrite);
        }
        return output.ToArray();
    }

    private static void WriteAscii85Group(Stream output, List<byte> group, int bytesToWrite)
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

    private static byte[] DecodeRunLength(byte[] value, IMemoryStreamUtil memoryStreamUtil)
    {
        using MemoryStream output = memoryStreamUtil.GetSync();
        var position = 0;
        while (position < value.Length)
        {
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
            var headerReader = new PdfSyntaxReader(decoded, 0);
            var entries = new (int Number, int Offset)[count];
            for (var index = 0; index < count; index++)
            {
                entries[index] = (headerReader.ReadInteger(), headerReader.ReadInteger());
            }

            for (var index = 0; index < count; index++)
            {
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

internal sealed class PdfSyntaxReader
{
    private readonly byte[] _data;

    internal PdfSyntaxReader(byte[] data, int position)
    {
        _data = data;
        Position = position;
    }

    internal int Position { get; set; }

    internal byte Current => Position < _data.Length ? _data[Position] : (byte)0;

    internal bool End => Position >= _data.Length;

    internal PdfValue ReadValue()
    {
        SkipWhiteSpaceAndComments();
        if (Position >= _data.Length)
            throw Error("Unexpected end of PDF data.");

        return _data[Position] switch
        {
            (byte)'<' when Peek(1) == (byte)'<' => ReadDictionary(),
            (byte)'<' => ReadHexString(),
            (byte)'(' => ReadLiteralString(),
            (byte)'[' => ReadArray(),
            (byte)'/' => ReadName(),
            (byte)'+' or (byte)'-' or (byte)'.' or >= (byte)'0' and <= (byte)'9' => ReadNumberOrReference(),
            _ => ReadKeywordValue()
        };
    }

    internal int ReadInteger()
    {
        SkipWhiteSpaceAndComments();
        PdfNumber number = ReadNumber();
        if (!number.IsInteger)
            throw Error("An integer was expected.");
        return number.AsInt32();
    }

    internal void SkipWhiteSpaceAndComments()
    {
        while (Position < _data.Length)
        {
            if (IsWhiteSpace(_data[Position]))
            {
                Position++;
                continue;
            }

            if (_data[Position] != (byte)'%')
                return;
            while (Position < _data.Length && _data[Position] is not ((byte)'\r') and not ((byte)'\n'))
                Position++;
        }
    }

    internal bool TryReadKeyword(string keyword)
    {
        SkipWhiteSpaceAndComments();
        ReadOnlySpan<byte> bytes = Encoding.ASCII.GetBytes(keyword);
        if (Position + bytes.Length > _data.Length || !_data.AsSpan(Position, bytes.Length).SequenceEqual(bytes))
            return false;
        if (Position + bytes.Length < _data.Length && !IsDelimiterOrWhiteSpace(_data[Position + bytes.Length]))
            return false;
        Position += bytes.Length;
        return true;
    }

    internal string ReadOperatorToken()
    {
        SkipWhiteSpaceAndComments();
        return ReadToken();
    }

    internal void ConsumeStreamLineEnding()
    {
        if (Position < _data.Length && _data[Position] == (byte)'\r')
            Position++;
        if (Position < _data.Length && _data[Position] == (byte)'\n')
            Position++;
    }

    internal static bool IsDelimiterOrWhiteSpace(byte value) => IsWhiteSpace(value) || value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or
        (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private PdfDictionary ReadDictionary()
    {
        Position += 2;
        var items = new Dictionary<string, PdfValue>(StringComparer.Ordinal);
        while (true)
        {
            SkipWhiteSpaceAndComments();
            if (Peek(0) == (byte)'>' && Peek(1) == (byte)'>')
            {
                Position += 2;
                return new PdfDictionary(items);
            }

            PdfName key = ReadName();
            items[key.Value] = ReadValue();
        }
    }

    private PdfArray ReadArray()
    {
        Position++;
        var values = new List<PdfValue>();
        while (true)
        {
            SkipWhiteSpaceAndComments();
            if (Peek(0) == (byte)']')
            {
                Position++;
                return new PdfArray(values);
            }
            values.Add(ReadValue());
        }
    }

    private PdfName ReadName()
    {
        if (Peek(0) != (byte)'/')
            throw Error("A PDF name was expected.");
        Position++;
        var bytes = new List<byte>();
        while (Position < _data.Length && !IsDelimiterOrWhiteSpace(_data[Position]))
        {
            if (_data[Position] == (byte)'#' && Position + 2 < _data.Length && TryHex(_data[Position + 1], out int high) && TryHex(_data[Position + 2], out int low))
            {
                bytes.Add((byte)(high * 16 + low));
                Position += 3;
            }
            else
            {
                bytes.Add(_data[Position++]);
            }
        }
        return new PdfName(Encoding.Latin1.GetString(CollectionsMarshal.AsSpan(bytes)));
    }

    private PdfString ReadLiteralString()
    {
        Position++;
        var depth = 1;
        var bytes = new List<byte>();
        while (Position < _data.Length && depth > 0)
        {
            byte value = _data[Position++];
            if (value == (byte)'\\')
            {
                if (Position >= _data.Length)
                    break;
                byte escaped = _data[Position++];
                switch (escaped)
                {
                    case (byte)'n': bytes.Add((byte)'\n'); break;
                    case (byte)'r': bytes.Add((byte)'\r'); break;
                    case (byte)'t': bytes.Add((byte)'\t'); break;
                    case (byte)'b': bytes.Add((byte)'\b'); break;
                    case (byte)'f': bytes.Add((byte)'\f'); break;
                    case (byte)'\r':
                        if (Peek(0) == (byte)'\n') Position++;
                        break;
                    case (byte)'\n': break;
                    case >= (byte)'0' and <= (byte)'7':
                        var octal = escaped - (byte)'0';
                        for (var count = 1; count < 3 && Position < _data.Length && _data[Position] is >= (byte)'0' and <= (byte)'7'; count++)
                            octal = octal * 8 + _data[Position++] - (byte)'0';
                        bytes.Add((byte)octal);
                        break;
                    default: bytes.Add(escaped); break;
                }
            }
            else if (value == (byte)'(')
            {
                depth++;
                bytes.Add(value);
            }
            else if (value == (byte)')')
            {
                depth--;
                if (depth > 0) bytes.Add(value);
            }
            else
            {
                bytes.Add(value);
            }
        }

        if (depth != 0)
            throw Error("An unterminated PDF string was encountered.");
        return new PdfString(bytes.ToArray());
    }

    private PdfString ReadHexString()
    {
        Position++;
        var digits = new List<int>();
        while (Position < _data.Length && _data[Position] != (byte)'>')
        {
            if (TryHex(_data[Position++], out int digit))
                digits.Add(digit);
        }
        if (Position >= _data.Length)
            throw Error("An unterminated PDF hex string was encountered.");
        Position++;
        if (digits.Count % 2 != 0)
            digits.Add(0);
        var value = new byte[digits.Count / 2];
        for (var index = 0; index < value.Length; index++)
            value[index] = (byte)(digits[index * 2] * 16 + digits[index * 2 + 1]);
        return new PdfString(value, true);
    }

    private PdfValue ReadNumberOrReference()
    {
        PdfNumber first = ReadNumber();
        if (!first.IsInteger)
            return first;

        int afterFirst = Position;
        SkipWhiteSpaceAndComments();
        if (Position < _data.Length && _data[Position] is >= (byte)'0' and <= (byte)'9')
        {
            PdfNumber second = ReadNumber();
            if (second.IsInteger)
            {
                SkipWhiteSpaceAndComments();
                if (TryReadKeyword("R"))
                    return new PdfReference(first.AsInt32(), second.AsInt32());
            }
        }
        Position = afterFirst;
        return first;
    }

    private PdfNumber ReadNumber()
    {
        int start = Position;
        if (Peek(0) is (byte)'+' or (byte)'-')
            Position++;
        bool decimalPoint = false;
        while (Position < _data.Length && (_data[Position] is >= (byte)'0' and <= (byte)'9' || !decimalPoint && _data[Position] == (byte)'.'))
        {
            decimalPoint |= _data[Position] == (byte)'.';
            Position++;
        }
        string text = Encoding.ASCII.GetString(_data, start, Position - start);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw Error($"Invalid PDF number '{text}'.");
        return new PdfNumber(value, !decimalPoint);
    }

    private PdfValue ReadKeywordValue()
    {
        string keyword = ReadToken();
        return keyword switch
        {
            "true" => new PdfBoolean(true),
            "false" => new PdfBoolean(false),
            "null" => PdfNull.Instance,
            _ => throw Error($"Unexpected PDF token '{keyword}'.")
        };
    }

    private string ReadToken()
    {
        int start = Position;
        while (Position < _data.Length && !IsDelimiterOrWhiteSpace(_data[Position]))
            Position++;
        return Encoding.ASCII.GetString(_data, start, Position - start);
    }

    private byte Peek(int offset) => Position + offset < _data.Length ? _data[Position + offset] : (byte)0;

    private InvalidDataException Error(string message) => new($"{message} (byte {Position})");

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
