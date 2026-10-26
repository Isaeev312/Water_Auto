using System.Security;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace WaterAuto.Reports;

/// <summary>Единый шрифт docx: все явно заданные шрифты документа, стилей, колонтитулов и темы заменяются выбранным.</summary>
internal static class DocxFont
{
    private static readonly Regex Typeface = new("typeface=\"[^\"]+\"", RegexOptions.Compiled);

    public static void Apply(MainDocumentPart main, string font)
    {
        OpenXmlPartRootElement?[] roots =
        [
            main.Document,
            main.StyleDefinitionsPart?.Styles,
            .. main.HeaderParts.Select(h => h.Header),
            .. main.FooterParts.Select(f => f.Footer),
        ];
        foreach (var root in roots.OfType<OpenXmlPartRootElement>())
        {
            foreach (var f in root.Descendants<RunFonts>())
            {
                f.Ascii = font;
                f.HighAnsi = font;
                f.ComplexScript = font;
                f.EastAsia = font;
            }
        }

        if (main.ThemePart is { } theme)
        {
            string xml;
            using (var reader = new StreamReader(theme.GetStream(FileMode.Open)))
            {
                xml = reader.ReadToEnd();
            }

            xml = Typeface.Replace(xml, $"typeface=\"{SecurityElement.Escape(font)}\"");
            using var writer = new StreamWriter(theme.GetStream(FileMode.Create));
            writer.Write(xml);
        }
    }
}
