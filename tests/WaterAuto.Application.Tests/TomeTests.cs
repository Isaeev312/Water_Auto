namespace WaterAuto.Application.Tests;

public class TomeTests
{
    [Fact]
    public void Tome_documents_follow_the_set_order_and_skip_what_was_not_issued()
    {
        // Общие данные — первыми, листы — по ведомости, этажи — по номеру, спецификации — в конце; plan.dxf (проверочный) в том не идёт.
        string[] written = ["out/specification.docx", "out/plan_level2.dxf", "out/plan.dwg", "out/plan.dxf", "out/pump_scheme.dxf", "out/plan_level1.dxf", "out/general_data.docx", "out/note.docx"];
        Tome.Ordered(written, Tome.AuptOrder).ShouldBe(["general_data.docx", "pump_scheme.dxf", "plan.dwg", "plan_level1.dxf", "plan_level2.dxf", "specification.docx"]);
    }

    [Fact]
    public void Design_stage_documents_take_the_section_mark()
    {
        // Стадия «П» (том ИОС2.2 образца): комплект — шифр раздела, спецификации — «.СО», записка — «.ПЗ»; стадия «Р» — как было.
        var pd = new ProjectFile { Title = new ProjectTitle { Stage = "П", SectionMark = "ИОС2.2" } };
        ProjectPipeline.DesignMark(pd, "ПТ").ShouldBe("ИОС2.2");
        ProjectPipeline.DesignMark(pd, "ПТ2.СО").ShouldBe("ИОС2.2.СО");
        ProjectPipeline.DesignMark(pd, "ПЗ").ShouldBe("ИОС2.2.ПЗ");
        // АПС и СОУЭ — своя часть (по умолчанию «ПБ2»: ПП РФ № 87, п. 26).
        ProjectPipeline.DesignMark(pd, "АПС").ShouldBe("ПБ2");
        ProjectPipeline.DesignMark(pd, "АПС.ПЗ").ShouldBe("ПБ2.ПЗ");
        ProjectPipeline.DesignMark(pd, "АПС.СО").ShouldBe("ПБ2.СО");
        ProjectPipeline.DesignMark(new ProjectFile(), "АПС.СО").ShouldBe("АПС.СО");
        ProjectPipeline.DesignMark(new ProjectFile(), "ПТ").ShouldBe("ПТ");
    }

    [Fact]
    public void Contents_pages_are_written_as_ranges()
    {
        WaterAuto.Export.TomePagesPdf.Pages(3, 30).ShouldBe("3–32");
        WaterAuto.Export.TomePagesPdf.Pages(33, 1).ShouldBe("33");
    }
}
