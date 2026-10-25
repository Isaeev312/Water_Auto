using System.Globalization;

namespace WaterAuto.Core;

/// <summary>Давление, МПа (единица ядра, README §5.1).</summary>
public readonly record struct Pressure(double Mpa)
{
    public static Pressure Zero => new(0);

    /// <summary>Геодезическое давление столба высотой h: Z = H/100 (Б.30).</summary>
    public static Pressure FromHeight(Length height) => new(height.Meters / 100.0);

    public static Pressure operator +(Pressure a, Pressure b) => new(a.Mpa + b.Mpa);
    public static Pressure operator -(Pressure a, Pressure b) => new(a.Mpa - b.Mpa);

    public override string ToString() => Mpa.ToString("0.######", CultureInfo.InvariantCulture) + " МПа";
}

/// <summary>Расход, л/с.</summary>
public readonly record struct Flow(double LitersPerSecond)
{
    public static Flow Zero => new(0);

    public static Flow operator +(Flow a, Flow b) => new(a.LitersPerSecond + b.LitersPerSecond);

    public override string ToString() => LitersPerSecond.ToString("0.####", CultureInfo.InvariantCulture) + " л/с";
}

/// <summary>Длина или отметка, м.</summary>
public readonly record struct Length(double Meters)
{
    /// <summary>Геометрия труб и чертежей — в мм (README §6).</summary>
    public static Length FromMillimeters(double mm) => new(mm / 1000.0);

    public double Millimeters => Meters * 1000.0;

    public override string ToString() => Meters.ToString("0.###", CultureInfo.InvariantCulture) + " м";
}

/// <summary>Коэффициент производительности оросителя K, л/(с·МПа^0,5): q = 10·K·√P.</summary>
public readonly record struct KFactor(double Value)
{
    public override string ToString() => Value.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>Удельная характеристика трубопровода Kт, л²/с² (табл. Б.2): ΔP = Q²·L/(100·Kт).</summary>
public readonly record struct PipeCharacteristic(double Kt)
{
    /// <summary>
    /// Через удельное сопротивление A, с²/л² (табл. Б.1): ΔP = A·Q²·L/100, т. е. Kт = 1/A.
    /// A — табличный коэффициент той же природы, что Kт, поэтому передаётся числом.
    /// </summary>
    public static PipeCharacteristic FromResistivity(double a) => new(1.0 / a);

    public override string ToString() => Kt.ToString("0.######", CultureInfo.InvariantCulture);
}
