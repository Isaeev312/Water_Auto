namespace WaterAuto.Reports.Tests;

public class BibliographyTests
{
    private const string Csv = """"
        id,название,авторы,год,"нормы, на которые опирается",статус,где лежит,что взято,читается
        SP485-izm,СП 485.1311500.2020 «Установки пожаротушения автоматические. Нормы и правила проектирования» с изм. № 1 (введено с 01.01.2026) и № 2 (введено с 09.07.2026),ФГБУ ВНИИПО МЧС России,2020/2026,—,действует,путь,"табл. 6.1, Б.1",да
        M02,"Проектирование водяных и пенных автоматических установок пожаротушения","Мешман Л. М., Былинкин В. А.",2002,НПБ 88-2001,справочно,путь,"прил. 9, ""пример""",да
        """";

    [Fact]
    public void Registry_csv_is_parsed_with_quoted_commas_and_quotes()
    {
        var entries = LiteratureRegistry.Parse(new StringReader(Csv));
        entries.Count.ShouldBe(2);
        entries[1].Id.ShouldBe("M02");
        entries[1].Authors.ShouldBe("Мешман Л. М., Былинкин В. А.");
        entries[1].Year.ShouldBe("2002");
    }

    [Fact]
    public void Norm_is_formatted_as_a_code_and_title_with_missing_parts_marked()
    {
        var sp = LiteratureRegistry.Parse(new StringReader(Csv))[0];
        var text = Bibliography.Format(sp);
        text.ShouldStartWith("СП 485.1311500.2020. Установки пожаротушения автоматические. Нормы и правила проектирования : свод правил");
        text.ShouldContain("с изм. № 1 (введено с 01.01.2026) и № 2 (введено с 09.07.2026)");
        text.ShouldContain("[дополнить");
    }

    [Fact]
    public void Book_keeps_authors_title_and_year_and_marks_what_the_registry_lacks()
    {
        var book = LiteratureRegistry.Parse(new StringReader(Csv))[1];
        var text = Bibliography.Format(book);
        text.ShouldStartWith("Мешман Л. М., Былинкин В. А. Проектирование водяных и пенных автоматических установок пожаротушения.");
        text.ShouldContain("2002");
        text.ShouldContain("[дополнить");
    }

    [Fact]
    public void Laws_and_resolutions_come_before_codes_of_practice()
    {
        var law = new LiteratureEntry("FZ123", "Федеральный закон от 22.07.2008 № 123-ФЗ «Технический регламент о требованиях пожарной безопасности»", "—", "2008", "действует");
        var pp = new LiteratureEntry("PP87", "Постановление Правительства РФ от 16.02.2008 № 87 «О составе разделов проектной документации»", "—", "2008", "действует");
        var sp = LiteratureRegistry.Parse(new StringReader(Csv))[0];
        var sp7 = new LiteratureEntry("SP7", "СП 7.13130.2013 «Отопление, вентиляция и кондиционирование»", "МЧС России", "2013", "действует");
        var gost = new LiteratureEntry("G", "ГОСТ Р 59636-2021 «Установки пожаротушения автоматические»", "—", "2021", "действует");
        Bibliography.Order([sp, pp, law, sp7, gost]).Select(e => e.Id).ShouldBe(["FZ123", "PP87", "G", "SP7", "SP485-izm"]);
        Bibliography.Format(law).ShouldStartWith("Федеральный закон от 22.07.2008 № 123-ФЗ");
    }

    [Fact]
    public void Norms_come_first_then_the_rest_alphabetically()
    {
        var entries = LiteratureRegistry.Parse(new StringReader(Csv));
        var list = Bibliography.Order([entries[1], entries[0]]);
        list[0].Id.ShouldBe("SP485-izm");
        list[1].Id.ShouldBe("M02");
    }
}
