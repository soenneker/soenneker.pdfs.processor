[![](https://img.shields.io/nuget/v/soenneker.pdfs.processor.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.pdfs.processor/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.pdfs.processor/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.pdfs.processor/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.pdfs.processor.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.pdfs.processor/)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.Pdfs.Processor
### A fast .NET utility for replacing text, merging, cleaning, and optimizing PDF documents.

## Installation

```bash
dotnet add package Soenneker.Pdfs.Processor
```

## Registration

```csharp
services.AddPdfProcessorAsSingleton();
```

## Replace text without changing its font operators

```csharp
PdfProcessResult result = await processor.ReplaceTextFile(
    "template.pdf",
    "completed.pdf",
    [
        new PdfTextReplacement
        {
            Search = "{{CustomerName}}",
            Replacement = "Jane Smith"
        }
    ],
    new PdfReplaceOptions
    {
        RequireAll = true,
        Output = new PdfOutputOptions
        {
            Compression = PdfCompressionProfile.Maximum
        }
    });
```

Replacement modifies supported PDF text-showing operands directly. The surrounding font, size, color, spacing, transform operators, and `TJ` positioning values are retained. Matches can span multiple string fragments within one text-showing operation, which handles the kerning-based fragmentation commonly produced by PDF writers. They can also span consecutive `Tj`/`TJ` operations when no positioning or graphics-state operator intervenes. Set `MatchAcrossTextFragments` or `MatchAcrossTextOperators` to `false` for stricter matching.

Custom/subset font encodings may not contain every replacement glyph, and matches never cross an intervening operator because it can represent a visual space, line break, font change, or transform. Inspect `PdfProcessResult.Replacements`, including `CrossFragmentReplacementCount`, to confirm each requested change.

## Merge pages structurally

```csharp
await processor.Merge(
[
    new PdfMergeSource { Stream = cover },
    new PdfMergeSource { Stream = body, StartPage = 2, EndPage = 8 }
], destination);
```

Pages and their PDF resources are imported without rasterization.

## Clean and optimize

```csharp
await processor.OptimizeFile(
    "input.pdf",
    "clean.pdf",
    new PdfOutputOptions
    {
        Compression = PdfCompressionProfile.Maximum,
        RemoveMetadata = true,
        RemoveEmbeddedFiles = true,
        RemoveJavaScriptAndActions = true
    });
```

Compression profiles are lossless. Cleanup can independently remove metadata, annotations, embedded files, JavaScript, and automatic actions.
