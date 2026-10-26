namespace WaterAuto.Reports.Tests;

public class BibliographyTests
{
    private const string Csv = """"
        id,название,авторы,год,"нормы, на которые опирается",статус,где лежит,что взято,читается,место издания,издательство,объём
        SP485-izm,СП 485.1311500.2020 «Установки пожаротушения автоматические. Нормы и правила проектирования» с изм. № 1 (введено с 01.01.2026) и № 2 (введено с 09.07.2026),ФГБУ ВНИИПО МЧС России,2020/2026,—,действует,путь,"табл. 6.1, Б.1",да,М.,ВНИИПО,100
        M02,"Проектирование водяных и пенных автоматических установок пожаротушения","Мешман Л. М., Былинкин В. А.",2002,НПБ 88-2001,справочно,путь,"прил. 9, ""пример""",да,М.,ВНИИПО,413
        NODATA,СП 1.13130.2020 «Системы противопожарной защиты. Эвакуационные пути и выходы»,—,2020,—,действует,путь,—,да,,,
        """";

    [Fact]
    public void Registry_csv_is_parsed_with_quoted_commas_and_quotes()
    {
        var entries = LiteratureRegistry.Parse(new StringReader(Csv));
        entries.Count.ShouldBe(3);
        entries[1].Id.ShouldBe("M02");
        entries[1].Authors.ShouldBe("Мешман Л. М., Былинкин В. А.");
        entries[1].Year.ShouldBe("2002");
    }

    [Fact]
    public void Bibliography_fields_place_publisher_volume_are_read_from_the_registry()
    {
        var entries = LiteratureRegistry.Parse(new StringReader(Csv));
        var sp = entries[0];
        sp.Place.ShouldBe("М.");
        sp.Publisher.ShouldBe("ВНИИПО");
        sp.Volume.ShouldBe("100");
    }

    [Fact]
    public void Norm_is_formatted_per_GOST_R_7_0_100_with_publication_data()
    {
        var sp = LiteratureRegistry.Parse(new StringReader(Csv))[0];
        var text = Bibliography.Format(sp);
        text.ShouldStartWith("СП 485.1311500.2020. Установки пожаротушения автоматические. Нормы и правила проектирования : свод правил");
        text.ShouldContain("с изм. № 1 (введено с 01.01.2026) и № 2 (введено с 09.07.2026)");
        text.ShouldContain("М. : ВНИИПО, 2020/2026. — 100 с.");
    }

    [Fact]
    public void Book_is_formatted_with_publication_data()
    {
        var book = LiteratureRegistry.Parse(new StringReader(Csv))[1];
        var text = Bibliography.Format(book);
        text.ShouldStartWith("Мешман Л. М., Былинкин В. А. Проектирование водяных и пенных автоматических установок пожаротушения.");
        text.ShouldContain("М. : ВНИИПО, 2002. — 413 с.");
    }

    [Fact]
    public void Missing_publication_parts_are_marked_not_invented()
    {
        var norm = LiteratureRegistry.Parse(new StringReader(Csv))[2];
        var text = Bibliography.Format(norm);
        text.ShouldContain("[место издания]");
        text.ShouldContain("[издательство]");
        text.ShouldContain("[объём]");
    }

    [Fact]
    public void Legal_act_has_no_publication_placeholders()
    {
        var law = new LiteratureEntry("FZ123", "Федеральный закон от 22.07.2008 № 123-ФЗ «Технический регламент о требованиях пожарной безопасности»", "—", "2008", "действует");
        var text = Bibliography.Format(law);
        text.ShouldBe("Федеральный закон от 22.07.2008 № 123-ФЗ «Технический регламент о требованиях пожарной безопасности». — 2008.");
        text.ShouldNotContain("[");
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
