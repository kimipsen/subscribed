using System.Globalization;
using System.Numerics;

namespace Subscribed.Subscriptions.Domain;

/// <summary>An exact rational number. All derived monetary values stay fractions until display (D-4).</summary>
public readonly struct Fraction : IEquatable<Fraction>
{
    public Fraction(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new DivideByZeroException();
        }

        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        if (!gcd.IsOne && !gcd.IsZero)
        {
            numerator /= gcd;
            denominator /= gcd;
        }

        _numerator = numerator;
        _denominator = numerator.IsZero ? BigInteger.One : denominator;
    }

    private readonly BigInteger _numerator;
    private readonly BigInteger _denominator;

    public BigInteger Numerator => _numerator;

    /// <summary>Always positive. A default-constructed fraction is zero.</summary>
    public BigInteger Denominator => _denominator.IsZero ? BigInteger.One : _denominator;

    public static Fraction Zero => default;

    public int Sign => Numerator.Sign;

    public bool IsZero => Numerator.IsZero;

    /// <summary>mantissa / 10^scale, read with <see cref="decimal.GetBits(decimal)"/>.</summary>
    public static Fraction FromDecimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        var mantissa = new BigInteger((uint)bits[2]) << 64 | new BigInteger((uint)bits[1]) << 32 | new BigInteger((uint)bits[0]);
        var scale = (bits[3] >> 16) & 0xFF;
        if (bits[3] < 0)
        {
            mantissa = -mantissa;
        }

        return new Fraction(mantissa, BigInteger.Pow(10, scale));
    }

    public static Fraction operator +(Fraction a, Fraction b) =>
        new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);

    public static Fraction operator -(Fraction a, Fraction b) =>
        new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);

    public static Fraction operator *(Fraction a, int n) => new(a.Numerator * n, a.Denominator);

    public static Fraction operator /(Fraction a, int n) => new(a.Numerator, a.Denominator * n);

    public static Fraction operator /(Fraction a, Fraction b) =>
        b.IsZero ? throw new DivideByZeroException() : new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);

    public static bool operator ==(Fraction a, Fraction b) => a.Equals(b);

    public static bool operator !=(Fraction a, Fraction b) => !a.Equals(b);

    public bool Equals(Fraction other) => Numerator == other.Numerator && Denominator == other.Denominator;

    public override bool Equals(object? obj) => obj is Fraction other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    public override string ToString() => $"{Numerator}/{Denominator}";
}

public static class Money
{
    public const int MaxIntegerDigits = 9;
    public const int MaxInputFractionDigits = 3;

    /// <summary>
    /// Shape only: <c>^[0-9]{1,9}(\.[0-9]{1,3})?$</c> with ASCII digits, so 0 ≤ amount &lt; 1,000,000,000 (BR-009). The minor-unit
    /// rule needs the currency and is checked with <see cref="HasAtMostFractionDigits"/>. The result keeps the typed scale.
    /// </summary>
    public static bool TryParseAmount(string? text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var integer = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? "" : text[(dot + 1)..];
        if (integer.Length is < 1 or > MaxIntegerDigits || !integer.All(IsAsciiDigit)
            || (dot >= 0 && (fraction.Length is < 1 or > MaxInputFractionDigits || !fraction.All(IsAsciiDigit))))
        {
            return false;
        }

        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    /// <summary>True when the decimal carries no more fraction digits than the currency's minor unit allows (scale, so JPY <c>1200.0</c> fails).</summary>
    public static bool HasAtMostFractionDigits(decimal amount, int minorUnit) => ScaleOf(amount) <= minorUnit;

    /// <summary>True when the value is a whole number of minor units, whatever its stored scale (D-11): DKK 129.00 → JPY is fine, 129.50 is not.</summary>
    public static bool IsWholeMinorUnits(decimal amount, int minorUnit)
    {
        var value = Fraction.FromDecimal(amount);
        return (value.Numerator * BigInteger.Pow(10, minorUnit) % value.Denominator).IsZero;
    }

    /// <summary>
    /// Invariant <c>F{minorUnit}</c> string. The value already fits the minor unit, so formatting only pads or drops trailing
    /// zeros and never rounds a non-zero digit. Zero never carries a minus sign.
    /// </summary>
    public static string FormatAmount(decimal value, int minorUnit)
    {
        var text = value.ToString("F" + minorUnit.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return text.StartsWith('-') && text.All(static c => c is '-' or '.' or '0') ? text[1..] : text;
    }

    /// <summary>Rounds once, half away from zero, to <paramref name="decimals"/> fraction digits. Zero is always positive zero.</summary>
    public static decimal RoundHalfAwayFromZero(Fraction value, int decimals)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        var scaled = value.Numerator * BigInteger.Pow(10, decimals);
        var quotient = BigInteger.DivRem(scaled, value.Denominator, out var remainder);
        if (BigInteger.Abs(remainder) * 2 >= value.Denominator)
        {
            quotient += value.Sign;
        }

        var negative = quotient.Sign < 0;
        var magnitude = BigInteger.Abs(quotient);
        if (magnitude > MaxMantissa)
        {
            throw new OverflowException("The value does not fit a decimal.");
        }

        var lo = (int)(uint)(magnitude & uint.MaxValue);
        var mid = (int)(uint)((magnitude >> 32) & uint.MaxValue);
        var hi = (int)(uint)((magnitude >> 64) & uint.MaxValue);
        return new decimal(lo, mid, hi, negative && !magnitude.IsZero, (byte)decimals);
    }

    private static readonly BigInteger MaxMantissa = (BigInteger.One << 96) - 1;

    private static int ScaleOf(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
}
