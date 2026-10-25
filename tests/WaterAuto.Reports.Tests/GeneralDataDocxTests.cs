using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace WaterAuto.Reports.Tests;

public class GeneralDataDocxTests
{
    private static readonly GeneralData Data = new(
        "Подземная автостоянка. Автоматическая установка водяного пожаротушения. Общие данные",
        [new("1", "Общие данные", ""), new("4", "План на отм. −3,300 с расстановкой оросителей", "")],
        [new("СП 485.1311500.2020", "Установки пожаротушения автоматические. Нормы и правила проектирования", ""), new("ГОСТ 3262-75", "Трубы стальные водогазопроводные. Технические условия", "")],
        [new("Расход насоса", "л/с", "30,00"), new("Количество оросителей", "шт.", "294")],
        ["Рабочие чертежи выполнены в соответствии с СП 485.1311500.2020.", "Размеры — в мм, отметки — в м."]);

    private static (Body Body, string Text) Write()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-gd-{Guid.NewGuid():N}.docx");
        GeneralDataDocx.Write(path, Data);
        try
        {
            using var doc = WordprocessingDocument.Open(path, false);
            var body = (Body)doc.MainDocumentPart!.Document.Body!.CloneNode(true);
            return (body, body.InnerText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sheet_has_the_three_lists_of_GOST_R_21_101_and_instructions()
    {
        var (body, text) = Write();
        text.ShouldContain("Ведомость рабочих чертежей основного комплекта");
        text.ShouldContain("Ведомость ссылочных и прилагаемых документов");
        text.ShouldContain("Основные показатели");
        text.ShouldContain("Общие указания");
        body.Elements<Table>().Count().ShouldBe(3);
        text.ShouldContain("ГОСТ 3262-75");
        text.ShouldContain("294");
    }

    [Fact]
    public void Instructions_are_numbered()
    {
        var (_, text) = Write();
        text.ShouldContain("1 Рабочие чертежи");
        text.ShouldContain("2 Размеры");
    }
}
