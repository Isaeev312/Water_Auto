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
}
