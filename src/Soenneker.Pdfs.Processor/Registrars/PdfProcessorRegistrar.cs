using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Pdfs.Processor.Abstract;
using Soenneker.Utils.File.Registrars;
using Soenneker.Utils.Path.Registrars;

namespace Soenneker.Pdfs.Processor.Registrars;

/// <summary>
/// A fast .NET utility for replacing text, merging, cleaning, and optimizing PDF documents.
/// </summary>
public static class PdfProcessorRegistrar
{
    /// <summary>
    /// Adds <see cref="IPdfProcessor"/> as a singleton service. <para/>
    /// </summary>
    public static IServiceCollection AddPdfProcessorAsSingleton(this IServiceCollection services)
    {
        services.AddFileUtilAsSingleton()
                .AddPathUtilAsSingleton()
                .TryAddSingleton<IPdfProcessor, PdfProcessor>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="IPdfProcessor"/> as a scoped service. <para/>
    /// </summary>
    public static IServiceCollection AddPdfProcessorAsScoped(this IServiceCollection services)
    {
        services.AddFileUtilAsSingleton()
                .AddPathUtilAsSingleton()
                .TryAddScoped<IPdfProcessor, PdfProcessor>();

        return services;
    }
}
