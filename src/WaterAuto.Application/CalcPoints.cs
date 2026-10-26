using System.Globalization;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Обозначения расчётных точек по расчётному направлению, как в приложении Б: оросители — 1, 2, 3… по ходу от диктующего,
/// узлы — а, б, в…, точка подключения — ВВ. Одни и те же — в таблице гидравлического расчёта ПЗ и на аксонометрической схеме.
/// </summary>
public static class CalcPoints
{
    private const string Letters = "абвгдежзиклмнопрстуфхцчшэюя";

    /// <summary>Узел трассы → обозначение, в порядке обхода направления от диктующего оросителя к вводу.</summary>
    public static IReadOnlyDictionary<string, string> Labels(DesignResult d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var kinds = d.Route.Nodes.ToDictionary(n => n.Id, n => n.Kind);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var (sprinklerNo, letterNo) = (0, 0);
        void Add(string id)
        {
            if (labels.ContainsKey(id))
            {
                return;
            }

            labels[id] = kinds.GetValueOrDefault(id) switch
            {
                RouteNodeKind.Sprinkler => (++sprinklerNo).ToString(CultureInfo.InvariantCulture),
                RouteNodeKind.Inlet => "ВВ",
                _ => letterNo < Letters.Length ? Letters[letterNo++].ToString() : $"у{++letterNo}",
            };
        }

        foreach (var x in d.Path.Reverse())
        {
            Add(x.To);
            Add(x.From);
        }

        return labels;
    }
}
