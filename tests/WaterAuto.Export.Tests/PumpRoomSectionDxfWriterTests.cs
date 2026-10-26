using ACadSharp;
using ACadSharp.Entities;
using WaterAuto.Core;

namespace WaterAuto.Export.Tests;

/// <summary>
/// Юнит-тесты разреза 1-1 насосной: лист в рамке, ожидаемые слои, отсутствие NaN и неверного знака отметок.
/// До этого теста <see cref="PumpRoomSectionDxfWriter"/> в выпуске не проверялся (аудит PD-15).
/// </summary>
public class PumpRoomSectionDxfWriterTests
{
    private static readonly TitleBlock Stamp = new("123-ПТ", "Автостоянка", "Корпус 1", "Разрез 1-1 насосной станции", "Р", "Организация", []);

    // Помещение 12 × 8 м, пол −3,000, перекрытие +1,500, земля 0,000; установка на фундаменте 150 мм, трубы на 0,24…2,4 м.
    private static PumpRoomSection Data() => new(
        1000,
        13000,
        300,
        -3.0,
        4.5,
        3.0,
        (2000, 2800, 0.6),
        [
            new("1. Установка", 2000, 3000, 0.15, 1.527, Pumps: 2, Position: 1),
            new("1. Прибор управления", 3700, 4300, 0, 1.46, Position: 1),
        ],
        [
            new("DN125", 1000, 13000, 2.4, [(1000, 1.4, 2.4), (13000, 4.2, 2.4)]),
            new("DN32", 2000, 4050, 1.5, [(4050, 0.6, 1.5)]),
        ],
        ["Тестовая заметка."]);

    [Fact]
    public void Section_has_expected_layers_frame_and_marks_without_NaN()
    {
        var doc = PumpRoomSectionDxfWriter.Build(Data(), Stamp);
        var texts = Texts(doc);

        var count = doc.Entities.Count;
        count.ShouldBeGreaterThan(50);
        count.ShouldBeLessThan(1000);
        var layers = doc.Entities.Select(e => e.Layer.Name).Distinct().ToList();
        layers.ShouldContain(PumpRoomSectionDxfWriter.BuildingLayer);
        layers.ShouldContain("ПТ_НС_ОТМЕТКИ");
        layers.ShouldContain(PumpRoomDxfWriter.PipeLayer);
        layers.ShouldContain(PumpRoomDxfWriter.EquipmentLayer);
        layers.ShouldContain(PumpRoomDxfWriter.TextLayer);
        layers.ShouldContain(GostSheet.Layer);

        texts.ShouldContain("Разрез 1-1 насосной станции");
        // Отметки — абсолютные (пол −3,000, ось трубы −0,600), без «-+».
        texts.ShouldContain("−3,000 пол");
        texts.ShouldContain("−0,600 ось трубы");
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
