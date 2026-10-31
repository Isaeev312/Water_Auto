using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using DocumentFormat.OpenXml.Packaging;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;
using WaterAuto.Import;

namespace WaterAuto.Application.Tests;

/// <summary>
/// Ручной выбор комплектной установки из каталога (`design.pumpStation.setDesignation`) в сквозном расчёте: установка
/// попадает в ПТ2.СО и пояснительную записку, число насосов в выпуске — по фактической установке, а не 1 + 1;
/// неизвестное обозначение и недостаточный напор дают объект Issue, а не исключение.
/// </summary>
public sealed class PumpSetPipelineTests : IDisposable
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly string _dir = Directory.CreateTempSubdirectory("wa-pump-set-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // Этаж 30 × 20 м; помещение насосной 12 × 8 м, два ввода, выпуск, два патрубка ГМ (как в PumpRoomPipelineTests).
    private string Drawing()
    {
        var doc = new CadDocument(ACadVersion.AC1032);
        doc.Header.InsUnits = UnitsType.Millimeters;
        Layer L(string name)
        {
            if (!doc.Layers.TryGetValue(name, out var layer))
            {
                layer = new Layer(name);
                doc.Layers.Add(layer);
            }

            return layer;
        }

        LwPolyline Rect(double x0, double y0, double x1, double y1) =>
            new([new XY(x0, y0), new XY(x1, y0), new XY(x1, y1), new XY(x0, y1)]) { IsClosed = true };
        var floor = Rect(0, 0, 30000, 20000);
        floor.Layer = L(FloorPlanImporter.ContourLayer);
        doc.Entities.Add(floor);
        doc.Entities.Add(new TextEntity("Автостоянка") { InsertPoint = new XYZ(15000, 10000, 0), Height = 250, Layer = L(FloorPlanImporter.ContourLayer) });
        doc.Entities.Add(new Point(new XYZ(500, 500, 0)) { Layer = L(FloorPlanImporter.InletLayer) });
        var room = Rect(1000, 1000, 13000, 9000);
        room.Layer = L(FloorPlanImporter.PumpRoomLayer);
        doc.Entities.Add(room);
        doc.Entities.Add(new Point(new XYZ(13000, 5000, 0)) { Layer = L(FloorPlanImporter.PumpRoomLayer) });
        foreach (var y in new[] { 3000.0, 5000.0 })
        {
            doc.Entities.Add(new Point(new XYZ(1000, y, 0)) { Layer = L(FloorPlanImporter.PumpInletLayer) });
        }

        doc.Entities.Add(new Point(new XYZ(13000, 3000, 0)) { Layer = L(FloorPlanImporter.PumpOutletLayer) });
        foreach (var y in new[] { 7000.0, 8000.0 })
        {
            doc.Entities.Add(new Point(new XYZ(13000, y, 0)) { Layer = L(FloorPlanImporter.PumpTruckLayer) });
        }

        var path = Path.Combine(_dir, "floor.dxf");
        DxfWriter.Write(path, doc, false);
        return path;
    }

    // Проект с насосной; setDesignation = "" — автоподбор установки по Q и H, иначе — выбранная из каталога.
    private string Project(string setDesignation = "") =>
        Write("project.waproj", ProjectFile.ToJson(new ProjectFile
        {
            Name = "Тестовая автостоянка с насосной",
            Drawing = "floor.dxf",
            Design = new ProjectDesign
            {
                RoomGroup = "2",
                SprinklerK = 0.47,
                SprinklerModel = "по каталогу",
                PipeElevation = -1.5,
                DictatingAboveAxis = 3.5,
                SupplyLength = 30,
                SupplyDn = 125,
                InletPressure = 0.1,
                DesignVelocity = 5,
                PumpRoom = new ProjectPumpRoom
                {
                    FloorElevation = -3,
                    GroundElevation = 0,
                    Height = 4.5,
                    Underground = true,
                },
                PumpStation = new ProjectPumpStation { SetDesignation = setDesignation },
            },
        }));

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private PipelineResult Run(string project) =>
        ProjectPipeline.Run(project, Path.Combine(_dir, "out"), RepoPaths.Norms, RepoPaths.Registry);

    private static string IssueText(PipelineResult result) =>
        string.Join("\n", result.Issues.Select(i => $"{i.Level} {i.Code}: {i.Message}"));

    // Установки каталога, у которых напор по кривой при требуемой подаче не меньше требуемого (критерий PumpSetSelection).
    private static List<(PumpSet Set, double Head)> FittingSets(PipelineResult result)
    {
        var q = result.Design!.PumpFlow.LitersPerSecond * 3.6;
        var h = result.Design.PumpHead.Meters;
        return
        [
            .. PumpCatalog.Sets(RepoPaths.Norms)
                .Select(s => (Set: s, Head: PumpSetSelection.HeadAt(s, q)))
                .Where(x => x.Head is { } head && head >= h)
                .Select(x => (Set: x.Set, Head: x.Head!.Value))
                .OrderBy(x => x.Set.PowerKw * x.Set.MainPumps)
                .ThenBy(x => x.Head),
        ];
    }

    private static string DocxText(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return doc.MainDocumentPart!.Document.Body!.InnerText;
    }

    private static string FileOf(PipelineResult result, string name) =>
        result.Files.Single(f => f.EndsWith(name, StringComparison.Ordinal));

    [Fact]
    public void Signal_list_follows_the_station_specification()
    {
        Drawing();
        var result = Run(Project());
        result.Success.ShouldBeTrue(IssueText(result));
        result.Pump.ShouldNotBeNull();

        var rows = WaterAuto.Reports.XlsxPreview.ReadFirstSheet(FileOf(result, ProjectPipeline.SignalListFile));
        var ids = rows.Skip(WaterAuto.Reports.SignalListXlsx.HeaderRow).Select(r => r[1]).ToList();

        // Два ввода в насосную — две задвижки; одна секция — затворы до и после узла и его сигнализатор давления.
        ids.ShouldBe(["inletValve.1", "inletValve.2", "cu.1", "cuValve.1", "cuValve.2"], ignoreOrder: true);
        // Затворы обвязки входят в комплектную установку и в перечень не попадают, как и в ПТ2.СО.
        ids.ShouldNotContain(id => id.StartsWith("valveS", StringComparison.Ordinal) || id.StartsWith("valveD", StringComparison.Ordinal));
        result.Issues.ShouldContain(i => i.Code == "aps.signals.incomplete");
    }

    [Fact]
    public void Auto_selection_fills_the_pump_summary()
    {
        Drawing();
        var result = Run(Project());
        result.Success.ShouldBeTrue(IssueText(result));
        result.Pump.ShouldNotBeNull();
        PumpCatalog.Sets(RepoPaths.Norms).ShouldContain(s => s.Designation == result.Pump!.Designation);
        result.Pump!.MainPumps.ShouldBeGreaterThan(0);
        result.Pump.HeadAtDutyMeters.ShouldBeGreaterThanOrEqualTo(result.Design!.PumpHead.Meters);
        // Данные для карточки и графика Q–H: требуемые подача/напор и паспортная кривая насоса.
        result.Pump.RequiredFlowM3h.ShouldBe(result.Design.PumpFlow.LitersPerSecond * 3.6, 1e-9);
        result.Pump.RequiredHeadMeters.ShouldBe(result.Design.PumpHead.Meters, 1e-9);
        result.Pump.Curve.Count.ShouldBeGreaterThan(1);
        result.Pump.Curve.Select(p => p.QM3h).ShouldBe(result.Pump.Curve.Select(p => p.QM3h).OrderBy(q => q));
        result.Issues.ShouldContain(i => i.Code == "pump.setChosen" && i.Level == Core.IssueLevel.Info, IssueText(result));

        // Сводка помещения насосной и габаритов установки для карточки главного окна: помещение 12 × 8 м из чертежа,
        // разметка вводов/выпусков/патрубков ГМ и размеры выбранной установки из каталога.
        result.PumpRoom.ShouldNotBeNull();
        var room = result.PumpRoom!;
        room.RoomId.ShouldNotBeNull();
        room.RoomLengthM.ShouldBe(12.0);
        room.RoomWidthM.ShouldBe(8.0);
        room.RoomAreaM2.ShouldBe(96.0);
        room.Inlets.ShouldBe(2);
        room.Outlets.ShouldBe(1);
        room.TruckInlets.ShouldBe(2);
        room.SetDesignation.ShouldBe(result.Pump!.Designation);
        room.MainPumps.ShouldBe(result.Pump.MainPumps);
        room.StandbyPumps.ShouldBe(result.Pump.StandbyPumps);
        room.SetHydraulic.LengthMm.ShouldBeGreaterThan(0);
        room.SetHydraulic.WidthMm.ShouldBeGreaterThan(0);
        room.SetHydraulic.HeightMm.ShouldBeGreaterThan(0);
        room.SetCabinet.LengthMm.ShouldBeGreaterThan(0);
        room.SetCabinet.WidthMm.ShouldBeGreaterThan(0);
        room.SetCabinet.HeightMm.ShouldBeGreaterThan(0);
        room.CollectorDn.ShouldBeGreaterThan(0);
        room.CollectorAxisMm.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Manual_set_from_the_catalog_is_used_in_the_specification_and_the_note()
    {
        Drawing();
        var auto = Run(Project());
        auto.Success.ShouldBeTrue(IssueText(auto));

        // Предпочтение — установке с двумя основными насосами: количество в выпуске должно быть по установке, а не 1 + 1.
        var fitting = FittingSets(auto);
        var twoMain = fitting.Where(x => x.Set.MainPumps == 2).ToList();
        var manual = twoMain.Count > 0 ? twoMain[0].Set : fitting[0].Set;
        var result = Run(Project(manual.Designation));
        result.Success.ShouldBeTrue(IssueText(result));
        result.Pump.ShouldNotBeNull();
        result.Pump!.Designation.ShouldBe(manual.Designation);
        result.Pump.MainPumps.ShouldBe(manual.MainPumps);
        result.Pump.StandbyPumps.ShouldBe(manual.StandbyPumps);
        result.Issues.ShouldContain(
            i => i.Code == "pump.setChosen" && i.Level == Core.IssueLevel.Info && i.Message.Contains("Выбрана вручную", StringComparison.Ordinal),
            IssueText(result));

        var spec = DocxText(FileOf(result, ProjectPipeline.PumpStationSpecFile));
        spec.ShouldContain(manual.Designation);
        spec.ShouldContain(string.Create(Ru, $"{manual.MainPumps} основн. + {manual.StandbyPumps} резервн."));
        // Строка шаблона «Агрегат насосный…» заменяется строкой комплектной установки (насосы входят в комплект).
        spec.ShouldNotContain("Агрегат насосный");

        var note = DocxText(FileOf(result, ProjectPipeline.NoteFile));
        note.ShouldContain("Принята комплектная установка " + manual.Designation);
    }

    [Fact]
    public void Unknown_pump_set_is_reported_and_auto_selection_continues_without_exception()
    {
        Drawing();
        var result = Run(Project("Hydro MX-A 99/9 CR 999-9"));

        result.Success.ShouldBeFalse();
        result.Issues.ShouldContain(i => i.Code == "pump.setNotFound" && i.Level == Core.IssueLevel.Error, IssueText(result));
        // Ошибка не бросает исключение: расчёт идёт дальше и подбирает установку автоматически.
        result.Issues.ShouldContain(i => i.Code == "pump.setChosen" && i.Level == Core.IssueLevel.Info, IssueText(result));
        // Error насосной останавливает выпуск: остаётся только файл замечаний.
        result.Files.ShouldHaveSingleItem().ShouldEndWith(ProjectPipeline.IssuesFile);
    }

    [Fact]
    public void Pump_set_with_insufficient_head_stops_the_package()
    {
        Drawing();
        var auto = Run(Project());
        auto.Success.ShouldBeTrue(IssueText(auto));
        var required = auto.Design!.PumpHead.Meters;
        var q = auto.Design.PumpFlow.LitersPerSecond * 3.6;

        // Самая низконапорная установка каталога заведомо не покрывает требуемый напор реального проекта.
        var weakest = PumpCatalog.Sets(RepoPaths.Norms).OrderBy(s => s.PumpCurve.Max(p => p.HM)).First();
        var head = PumpSetSelection.HeadAt(weakest, q);
        (head is null || head < required).ShouldBeTrue($"установка {weakest.Designation} неожиданно подходит: H = {head}");

        var result = Run(Project(weakest.Designation));
        result.Success.ShouldBeFalse();
        var issue = result.Issues.Single(i => i.Code == "pump.setInsufficient");
        issue.Level.ShouldBe(Core.IssueLevel.Error);
        issue.Message.ShouldContain(weakest.Designation);
        issue.Message.ShouldContain(string.Create(Ru, $"требуется H = {required:0.0} м"));
        if (head is { } headAtDuty)
        {
            issue.Message.ShouldContain(string.Create(Ru, $"H = {headAtDuty:0.0} м"));
        }

        result.Files.ShouldHaveSingleItem().ShouldEndWith(ProjectPipeline.IssuesFile);
    }
}
