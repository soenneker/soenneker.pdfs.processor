using System.Globalization;

namespace Soenneker.Pdfs.Processor.Internal;

internal abstract record PdfValue;

internal sealed record PdfNull : PdfValue
{
    internal static readonly PdfNull Instance = new();
}

internal sealed record PdfBoolean(bool Value) : PdfValue;

internal sealed record PdfNumber(double Value, bool IsInteger = false) : PdfValue
{
    internal int AsInt32() => checked((int)Value);

    public override string ToString() => IsInteger
        ? ((long)Value).ToString(CultureInfo.InvariantCulture)
        : Value.ToString("0.################", CultureInfo.InvariantCulture);
}

internal sealed record PdfName(string Value) : PdfValue;

internal sealed record PdfString : PdfValue
{
    internal PdfString(byte[] value, bool hex = false)
    {
        Value = value;
        Hex = hex;
    }

    internal byte[] Value { get; set; }

    internal bool Hex { get; }
}

internal sealed record PdfReference(int ObjectNumber, int Generation = 0) : PdfValue;

internal sealed record PdfArray(List<PdfValue> Items) : PdfValue;

internal sealed record PdfDictionary(Dictionary<string, PdfValue> Items) : PdfValue
{
    internal PdfValue? Get(string name) => Items.GetValueOrDefault(name);

    internal PdfDictionary? GetDictionary(string name, PdfDocumentModel document) => document.Resolve(Get(name)) as PdfDictionary;

    internal PdfArray? GetArray(string name, PdfDocumentModel document) => document.Resolve(Get(name)) as PdfArray;

    internal string? GetName(string name, PdfDocumentModel document) => (document.Resolve(Get(name)) as PdfName)?.Value;

    internal int? GetInteger(string name, PdfDocumentModel document) => document.Resolve(Get(name)) is PdfNumber number ? number.AsInt32() : null;
}

internal sealed class PdfIndirectObject
{
    internal required int Number { get; init; }

    internal int Generation { get; init; }

    internal required PdfValue Value { get; set; }

    internal byte[]? StreamData { get; set; }
}

internal sealed class PdfDocumentModel
{
    internal Dictionary<int, PdfIndirectObject> Objects { get; } = [];

    internal PdfReference? RootReference { get; set; }

    internal PdfReference? InfoReference { get; set; }

    internal PdfReference? EncryptReference { get; set; }

    internal PdfDictionary Catalog => Resolve(RootReference) as PdfDictionary
                                      ?? throw new InvalidDataException("The PDF catalog could not be resolved.");

    internal PdfValue? Resolve(PdfValue? value)
    {
        if (value is not PdfReference)
            return value;

        var visited = new HashSet<int>();
        while (value is PdfReference reference)
        {
            if (!visited.Add(reference.ObjectNumber) || !Objects.TryGetValue(reference.ObjectNumber, out PdfIndirectObject? indirect))
                return null;
            value = indirect.Value;
        }

        return value;
    }

    internal PdfIndirectObject ResolveObject(PdfReference reference) => Objects.TryGetValue(reference.ObjectNumber, out PdfIndirectObject? value)
        ? value
        : throw new InvalidDataException($"PDF object {reference.ObjectNumber} could not be resolved.");

    internal List<PdfReference> GetPages()
    {
        PdfValue? pagesValue = Catalog.Get("Pages");
        if (pagesValue is not PdfReference pagesReference)
            throw new InvalidDataException("The PDF page tree could not be resolved.");

        var pages = new List<PdfReference>();
        CollectPages(pagesReference, pages, []);
        return pages;
    }

    private void CollectPages(PdfReference reference, List<PdfReference> pages, HashSet<int> visited)
    {
        if (!visited.Add(reference.ObjectNumber))
            throw new InvalidDataException("The PDF page tree contains a cycle.");

        PdfDictionary dictionary = Resolve(reference) as PdfDictionary
                                  ?? throw new InvalidDataException("A PDF page-tree object is not a dictionary.");
        string? type = dictionary.GetName("Type", this);
        if (type == "Page")
        {
            pages.Add(reference);
            return;
        }

        PdfArray kids = dictionary.GetArray("Kids", this)
                        ?? throw new InvalidDataException("A PDF page-tree node has no Kids array.");
        foreach (PdfValue kid in kids.Items)
        {
            if (kid is not PdfReference child)
                throw new InvalidDataException("A PDF page-tree child is not an indirect reference.");
            CollectPages(child, pages, visited);
        }
    }
}
