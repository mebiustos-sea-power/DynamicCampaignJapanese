using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace DynamicCampaignJapanese;

public sealed class MarkupTranslator
{
    private readonly TranslationCatalog catalog;

    public MarkupTranslator(TranslationCatalog catalog) => this.catalog = catalog;

    public string Translate(string markup)
    {
        using var input = new StringReader(markup);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        (document.Root ?? throw new XmlException("XAML has no root element."))
            .SetAttributeValue("TextElement.FontFamily", "Microsoft YaHei");
        foreach (var element in document.Descendants())
        foreach (var attribute in element.Attributes())
        {
            var name = attribute.Name.LocalName;
            if (attribute.Name.NamespaceName.Length != 0 ||
                (name != "Text" && name != "Content" && name != "ToolTip" && name != "Header" && name != "Value")) continue;
            if (catalog.Strings.TryGetValue(attribute.Value, out var translated)) attribute.Value = translated;
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }
}
