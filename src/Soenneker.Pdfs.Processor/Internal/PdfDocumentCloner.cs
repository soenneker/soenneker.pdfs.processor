namespace Soenneker.Pdfs.Processor.Internal;

internal static class PdfDocumentCloner
{
    private static readonly string[] InheritedPageKeys = ["Resources", "MediaBox", "CropBox", "Rotate"];

    internal static PdfDocumentModel CreateDocument()
    {
        var document = new PdfDocumentModel();
        var catalogReference = new PdfReference(1);
        var pagesReference = new PdfReference(2);
        document.Objects[1] = new PdfIndirectObject
        {
            Number = 1,
            Value = new PdfDictionary(new Dictionary<string, PdfValue>
            {
                ["Type"] = new PdfName("Catalog"),
                ["Pages"] = pagesReference
            })
        };
        document.Objects[2] = new PdfIndirectObject
        {
            Number = 2,
            Value = new PdfDictionary(new Dictionary<string, PdfValue>
            {
                ["Type"] = new PdfName("Pages"),
                ["Kids"] = new PdfArray([]),
                ["Count"] = new PdfNumber(0, true)
            })
        };
        document.RootReference = catalogReference;
        return document;
    }

    internal static PdfReference AddPage(PdfDocumentModel source, PdfReference sourcePageReference, PdfDocumentModel destination,
        Dictionary<int, PdfReference> map)
    {
        PdfDictionary sourcePage = (PdfDictionary)source.Resolve(sourcePageReference)!;
        var pageItems = new Dictionary<string, PdfValue>(sourcePage.Items, StringComparer.Ordinal);
        MaterializeInheritedPageValues(source, sourcePage, pageItems);
        pageItems.Remove("Parent");

        PdfDictionary destinationPages = (PdfDictionary)destination.Resolve(new PdfReference(2))!;
        var kids = (PdfArray)destinationPages.Items["Kids"];
        PdfReference pageReference;
        int number;
        if (map.TryGetValue(sourcePageReference.ObjectNumber, out PdfReference? mapped) && !kids.Items.Contains(mapped))
        {
            pageReference = mapped;
            number = mapped.ObjectNumber;
        }
        else
        {
            number = NextNumber(destination);
            pageReference = new PdfReference(number);
            destination.Objects[number] = new PdfIndirectObject { Number = number, Value = PdfNull.Instance };
        }
        map[sourcePageReference.ObjectNumber] = pageReference;

        var clonedItems = new Dictionary<string, PdfValue>(StringComparer.Ordinal);
        foreach ((string key, PdfValue value) in pageItems)
            clonedItems[key] = CloneValue(source, destination, value, map);

        clonedItems["Type"] = new PdfName("Page");
        clonedItems["Parent"] = new PdfReference(2);
        destination.Objects[number].Value = new PdfDictionary(clonedItems);

        kids.Items.Add(pageReference);
        destinationPages.Items["Count"] = new PdfNumber(kids.Items.Count, true);
        return pageReference;
    }

    internal static PdfReference CloneReference(PdfDocumentModel source, PdfDocumentModel destination, PdfReference reference,
        Dictionary<int, PdfReference> map)
    {
        if (map.TryGetValue(reference.ObjectNumber, out PdfReference? existing))
            return existing;

        PdfIndirectObject sourceObject = source.ResolveObject(reference);
        int number = NextNumber(destination);
        var destinationReference = new PdfReference(number);
        map[reference.ObjectNumber] = destinationReference;
        var destinationObject = new PdfIndirectObject { Number = number, Value = PdfNull.Instance };
        destination.Objects[number] = destinationObject;
        destinationObject.Value = CloneValue(source, destination, sourceObject.Value, map);
        destinationObject.StreamData = sourceObject.StreamData?.ToArray();
        return destinationReference;
    }

    internal static PdfValue CloneValue(PdfDocumentModel source, PdfDocumentModel destination, PdfValue value,
        Dictionary<int, PdfReference> map) => value switch
        {
            PdfNull => PdfNull.Instance,
            PdfBoolean boolean => boolean,
            PdfNumber number => number,
            PdfName name => name,
            PdfString text => new PdfString(text.Value.ToArray(), text.Hex),
            PdfReference reference => CloneReference(source, destination, reference, map),
            PdfArray array => new PdfArray(array.Items.Select(item => CloneValue(source, destination, item, map)).ToList()),
            PdfDictionary dictionary => new PdfDictionary(dictionary.Items
                .ToDictionary(item => item.Key, item => CloneValue(source, destination, item.Value, map), StringComparer.Ordinal)),
            _ => throw new InvalidDataException($"Unsupported PDF value type {value.GetType().Name}.")
        };

    private static void MaterializeInheritedPageValues(PdfDocumentModel document, PdfDictionary page, Dictionary<string, PdfValue> result)
    {
        PdfDictionary? current = page;
        var visited = new HashSet<int>();
        while (current != null)
        {
            foreach (string key in InheritedPageKeys)
            {
                if (!result.ContainsKey(key) && current.Get(key) is PdfValue inherited)
                    result[key] = inherited;
            }

            if (current.Get("Parent") is not PdfReference parent || !visited.Add(parent.ObjectNumber))
                break;
            current = document.Resolve(parent) as PdfDictionary;
        }
    }

    private static int NextNumber(PdfDocumentModel document) => document.Objects.Count == 0 ? 1 : document.Objects.Keys.Max() + 1;
}
