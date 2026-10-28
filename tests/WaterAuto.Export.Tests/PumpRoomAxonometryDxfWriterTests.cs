using ACadSharp;
using ACadSharp.Entities;
using WaterAuto.Core;

namespace WaterAuto.Export.Tests;

/// <summary>
/// Юнит-тесты аксонометрии обвязки насосной: лист в рамке, ожидаемые слои, отсутствие NaN и неверного знака отметок.
/// До этого теста <see cref="PumpRoomAxonometryDxfWriter"/> в выпуске не проверялся (аудит PD-15).
/// </summary>
public class PumpRoomAxonometryDxfWriterTests
{
    private static readonly TitleBlock Stamp = new("123-ПТ", "Автостоянка", "Корпус 1", "Аксонометрия обвязки насосной", "Р", "Организация", []);

    // Две нитки в пространстве: плановая изометрия (Y под 45°), отметки от пола (−3,000) → подписи абсолютные.
    private static PumpRoomAxonometry Data() => new(
        [
            new("DN125", [new Point3(1000, 1000, 1.4), new Point3(1000, 1000, 2.4), new Point3(4000, 3000, 2.4), new Point3(4000, 3000, 1.0)]),
            new("DN32", [new Point3(2000, 2000, 0.24), new Point3(2000, 2000, 1.5), new Point3(4050, 2400, 1.5)]),
        ],
        [new AxonoBlock("1. Установка", 2000, 2000, 3000, 3480, 0.15, 1.527)],
        [new AxonoValve(new Point3(2000, 2500, 2.4), "gate", 5)],
        ["Тестовая заметка."],
        -3.0);

    [Fact]
    public void Axonometry_has_expected_layers_frame_marks_and_no_NaN()
    {
        var doc = PumpRoomAxonometryDxfWriter.Build(Data(), Stamp);
        var texts = Texts(doc);

        var count = doc.Entities.Count;
        count.ShouldBeGreaterThan(30);
        count.ShouldBeLessThan(1000);
        var layers = doc.Entities.Select(e => e.Layer.Name).Distinct().ToList();
        layers.ShouldContain(PumpRoomAxonometryDxfWriter.PipeLayer);
        layers.ShouldContain(PumpRoomAxonometryDxfWriter.EquipmentLayer);
        layers.ShouldContain(PumpRoomDxfWriter.TextLayer);
        layers.ShouldContain(GostSheet.Layer);

        texts.ShouldContain("Аксонометрическая схема обвязки насосной станции");
        texts.ShouldContain("1. Установка");
        texts.ShouldContain("DN125 (В2)");
        // Отметки верха/низа вертикалей абсолютные и с правильным знаком: −0,600 / −1,600; без «-+».
        texts.ShouldContain("−0,600");
        texts.ShouldContain("−1,600");
        texts.ShouldNotContain(t => t.Contains("-+", StringComparison.Ordinal));
        AssertFinite(doc);
    }

    private static List<string> Texts(CadDocument doc)
    {
        var texts = new List<string>();
        foreach (var e in doc.Entities)
        {
            switch (e)
            {
                case TextEntity t:
                    texts.Add(t.Value);
                    break;
                case MText m:
                    texts.Add(m.Value);
                    break;
            }
        }

        return texts;
    }

    private static void AssertFinite(CadDocument doc)
    {
        static void Finite(double value, string what) => double.IsFinite(value).ShouldBeTrue(what);
        foreach (var e in doc.Entities)
        {
            switch (e)
            {
                case Line line:
                    Finite(line.StartPoint.X, "Line.X");
                    Finite(line.StartPoint.Y, "Line.Y");
                    Finite(line.EndPoint.X, "Line.X2");
                    Finite(line.EndPoint.Y, "Line.Y2");
                    break;
                case LwPolyline polyline:
                    foreach (var vertex in polyline.Vertices)
                    {
                        Finite(vertex.Location.X, "LwPolyline.X");
                        Finite(vertex.Location.Y, "LwPolyline.Y");
                    }

                    break;
                case TextEntity text:
                    Finite(text.InsertPoint.X, "Text.X");
                    Finite(text.InsertPoint.Y, "Text.Y");
                    Finite(text.Height, "Text.Height");
                    break;
                case MText mtext:
                    Finite(mtext.InsertPoint.X, "MText.X");
                    Finite(mtext.InsertPoint.Y, "MText.Y");
                    Finite(mtext.Height, "MText.Height");
                    break;
                case Arc arc:
                    Finite(arc.Center.X, "Arc.X");
                    Finite(arc.Center.Y, "Arc.Y");
                    Finite(arc.Radius, "Arc.Radius");
                    break;
                case Circle circle:
                    Finite(circle.Center.X, "Circle.X");
                    Finite(circle.Center.Y, "Circle.Y");
                    Finite(circle.Radius, "Circle.Radius");
                    break;
            }
        }
    }
}
