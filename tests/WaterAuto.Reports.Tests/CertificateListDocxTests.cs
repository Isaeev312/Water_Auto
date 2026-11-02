using DocumentFormat.OpenXml.Packaging;
using WaterAuto.Core;

namespace WaterAuto.Reports.Tests;

public sealed class CertificateListDocxTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-cert-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Certificates_go_to_their_own_table_with_spec_positions()
    {
        // Сертификаты — отдельным документом к спецификации, позиция — номер строки спецификации.
        var path = Path.Combine(_dir, "c.docx");
        CertificateListDocx.Write(path, [new CertificateRow("3", "Извещатель", "ДИП-34А-03", "НВП «Болид»", "ЕАЭС RU С-RU.ПБ68.В.01396/22")],
            new TitleBlock("2026-01-ПБ2.СО", "", "", "Перечень документов о подтверждении соответствия", "П", "", []));
        using var doc = WordprocessingDocument.Open(path, false);
        var text = doc.MainDocumentPart!.Document.Body!.InnerText;
        text.ShouldContain("Документ о подтверждении соответствия");
        text.ShouldContain("ЕАЭС RU С-RU.ПБ68.В.01396/22");
        doc.MainDocumentPart.Document.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableRow>().Count().ShouldBe(2);
    }
}
