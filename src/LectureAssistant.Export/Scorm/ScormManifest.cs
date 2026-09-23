using System.Text;
using System.Xml.Linq;

namespace LectureAssistant.Export.Scorm;

/// <summary>Builds <c>imsmanifest.xml</c> for a single-SCO SCORM 1.2 package (ADL Content Packaging 1.2).</summary>
internal static class ScormManifest
{
    public const string FileName = "imsmanifest.xml";

    internal static readonly XNamespace ImsCp = "http://www.imsproject.org/xsd/imscp_rootv1p1p2";
    internal static readonly XNamespace AdlCp = "http://www.adlnet.org/xsd/adlcp_rootv1p2";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private const string SchemaLocation =
        "http://www.imsproject.org/xsd/imscp_rootv1p1p2 imscp_rootv1p1p2.xsd " +
        "http://www.imsglobal.org/xsd/imsmd_rootv1p2p1 imsmd_rootv1p2p1.xsd " +
        "http://www.adlnet.org/xsd/adlcp_rootv1p2 adlcp_rootv1p2.xsd";

    /// <param name="files">Every file in the package except the manifest itself.</param>
    public static XDocument Build(string projectId, string title, string launchFile, IEnumerable<string> files)
    {
        var id = Identifier(projectId);

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(ImsCp + "manifest",
                new XAttribute("identifier", $"MANIFEST-{id}"),
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xmlns + "adlcp", AdlCp),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                new XAttribute(Xsi + "schemaLocation", SchemaLocation),
                new XElement(ImsCp + "metadata",
                    new XElement(ImsCp + "schema", "ADL SCORM"),
                    new XElement(ImsCp + "schemaversion", "1.2")),
                new XElement(ImsCp + "organizations",
                    new XAttribute("default", $"ORG-{id}"),
                    new XElement(ImsCp + "organization",
                        new XAttribute("identifier", $"ORG-{id}"),
                        new XElement(ImsCp + "title", title),
                        new XElement(ImsCp + "item",
                            new XAttribute("identifier", $"ITEM-{id}"),
                            new XAttribute("identifierref", $"RES-{id}"),
                            new XAttribute("isvisible", "true"),
                            new XElement(ImsCp + "title", title)))),
                new XElement(ImsCp + "resources",
                    new XElement(ImsCp + "resource",
                        new XAttribute("identifier", $"RES-{id}"),
                        new XAttribute("type", "webcontent"),
                        new XAttribute(AdlCp + "scormtype", "sco"),
                        new XAttribute("href", launchFile),
                        files.Select(f => new XElement(ImsCp + "file", new XAttribute("href", f)))))));
    }

    /// <summary>XML ID-safe token derived from the project id, so re-exports of a lecture keep the same identifiers.</summary>
    internal static string Identifier(string projectId)
    {
        var sb = new StringBuilder("LA-");
        foreach (var c in projectId)
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') sb.Append(c);
        return sb.Length > 3 ? sb.ToString() : "LA-lecture";
    }
}
