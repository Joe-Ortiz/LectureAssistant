using LectureAssistant.Core;
using LectureAssistant.Export.H5P;
using LectureAssistant.Export.Scorm;

namespace LectureAssistant.App.Services;

public sealed class ExporterCatalog
{
    public ILectureExporter Scorm { get; } = new ScormExporter();

    /// <param name="referencePackagePath">An Interactive Video .h5p exported from the instructor's own H5P platform, to match its library versions.</param>
    public ILectureExporter CreateH5P(string? referencePackagePath) =>
        new H5PExporter(new H5PExportOptions { ReferencePackagePath = referencePackagePath });
}
