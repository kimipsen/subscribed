using System.Globalization;
using Subscribed.Subscriptions.Domain;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public sealed class SubscriptionPrimitivesTests
{
    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    private static Fraction F(string s) => Fraction.FromDecimal(D(s));

    // ---- ids ------------------------------------------------------------------------------------------

    [Fact]
    public void Ids_are_32_lowercase_hex_and_round_trip_through_the_stream_key()
    {
        var id = SubscriptionIds.New(new FixedClock(DateTimeOffset.Parse("2026-11-13T12:00:00Z", CultureInfo.InvariantCulture)));
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.True(SubscriptionIds.TryParse(id, out var parsed));
        Assert.Equal(id, parsed);
        Assert.Equal("subscription-" + id, SubscriptionIds.StreamKey(id));
        Assert.True(SubscriptionIds.TryParseStreamKey(SubscriptionIds.StreamKey(id), out var fromKey));
        Assert.Equal(id, fromKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("options")]
    [InlineData("new")]
    [InlineData("0193A0000000000000000000000000AB")]
    [InlineData("0193a000-0000-0000-0000-0000000000ab")]
    [InlineData("0193a00000000000000000000000000")]
    [InlineData("0193a0000000000000000000000000abc")]
    [InlineData("0193a00000000000000000000000000g")]
    public void Ids_reject_anything_but_32_lowercase_hex(string? value) => Assert.False(SubscriptionIds.TryParse(value, out _));

    [Fact]
    public void Stream_keys_need_the_prefix_and_a_valid_id()
    {
        Assert.False(SubscriptionIds.TryParseStreamKey("household-default", out _));
        Assert.False(SubscriptionIds.TryParseStreamKey("subscription-nope", out _));
        Assert.False(SubscriptionIds.TryParseStreamKey(null, out _));
    }

    // ---- text -----------------------------------------------------------------------------------------

    [Fact]
    public void Name_boundaries_are_80_and_81_after_trimming()
    {
        Assert.True(SubscriptionText.TryNormalizeName(new string('a', 80), out _));
        Assert.True(SubscriptionText.TryNormalizeName("  " + new string('a', 80) + "  ", out var trimmed));
        Assert.Equal(80, trimmed.Length);
        Assert.False(SubscriptionText.TryNormalizeName(new string('a', 81), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\u0000b")]
    public void Name_rejects_empty_and_control_characters(string? raw) => Assert.False(SubscriptionText.TryNormalizeName(raw, out _));

    [Fact]
    public void Duplicate_looking_names_are_fine_and_unicode_is_kept()
    {
        Assert.True(SubscriptionText.TryNormalizeName("  Æblegrød ✓ ", out var name));
        Assert.Equal("Æblegrød ✓", name);
    }

    [Fact]
    public void Category_boundaries_are_40_and_41_and_whitespace_means_absent()
    {
        Assert.True(SubscriptionText.TryNormalizeCategory(new string('c', 40), out var ok));
        Assert.Equal(40, ok!.Length);
        Assert.False(SubscriptionText.TryNormalizeCategory(new string('c', 41), out _));
        Assert.True(SubscriptionText.TryNormalizeCategory("   ", out var none));
        Assert.Null(none);
        Assert.True(SubscriptionText.TryNormalizeCategory(null, out none));
        Assert.Null(none);
        Assert.False(SubscriptionText.TryNormalizeCategory("a\tb", out _));
    }

    [Fact]
    public void Notes_boundaries_are_1000_and_1001()
    {
        Assert.True(SubscriptionText.TryNormalizeNotes(new string('n', 1000), out var ok));
        Assert.Equal(1000, ok!.Length);
        Assert.False(SubscriptionText.TryNormalizeNotes(new string('n', 1001), out _));
    }

    [Fact]
    public void Notes_normalize_line_endings_allow_lf_and_reject_tab()
    {
        Assert.True(SubscriptionText.TryNormalizeNotes("a\r\nb\rc\nd", out var notes));
        Assert.Equal("a\nb\nc\nd", notes);
        Assert.True(SubscriptionText.TryNormalizeNotes(" \r\n ", out var none));
        Assert.Null(none);
        Assert.False(SubscriptionText.TryNormalizeNotes("a\tb", out _));
        Assert.False(SubscriptionText.TryNormalizeNotes("a\u0007b", out _));
    }

    // ---- currency -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("DKK", 2)]
    [InlineData("EUR", 2)]
    [InlineData("USD", 2)]
    [InlineData("SEK", 2)]
    [InlineData("NOK", 2)]
    [InlineData("GBP", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KWD", 3)]
    [InlineData("BHD", 3)]
    public void Minor_units(string code, int expected)
    {
        Assert.True(Iso4217.IsValid(code));
        Assert.True(Iso4217.TryGetMinorUnit(code, out var minor));
        Assert.Equal(expected, minor);
    }

    [Theory]
    [InlineData("XYZ")]
    [InlineData("dkk")]
    [InlineData("Dkk")]
    [InlineData("XXX")]
    [InlineData("XTS")]
    [InlineData("XAU")]
    [InlineData("CLF")]
    [InlineData("DK")]
    [InlineData("DKKK")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejected_currency_codes(string? code)
    {
        Assert.False(Iso4217.IsValid(code));
        Assert.False(Iso4217.TryGetMinorUnit(code, out _));
    }

    [Fact]
    public void Every_currency_has_a_minor_unit_of_0_2_or_3_and_codes_are_sorted()
    {
        Assert.NotEmpty(Iso4217.Codes);
        Assert.Equal(Iso4217.Codes.Order(StringComparer.Ordinal), Iso4217.Codes);
        foreach (var code in Iso4217.Codes)
        {
            Assert.True(Iso4217.TryGetMinorUnit(code, out var minor));
            Assert.Contains(minor, new[] { 0, 2, 3 });
        }

        foreach (var excluded in new[] { "BOV", "CHE", "CHW", "CLF", "COU", "MXV", "USN", "UYI", "UYW", "XAU", "XAG", "XPD", "XPT", "XBA", "XBB", "XBC", "XBD", "XDR", "XSU", "XUA", "XTS", "XXX" })
        {
            Assert.DoesNotContain(excluded, Iso4217.Codes);
        }
    }

    // ---- amounts --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0", "0")]
    [InlineData("0.00", "0.00")]
    [InlineData("129.00", "129.00")]
    [InlineData("999999999.99", "999999999.99")]
    [InlineData("1.234", "1.234")]
    [InlineData("11.99", "11.99")]
    public void Accepted_amounts_keep_their_typed_scale(string text, string expected)
    {
        Assert.True(Money.TryParseAmount(text, out var amount));
        Assert.Equal(expected, amount.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1000000000")]
    [InlineData("-5.00")]
    [InlineData("1.2345")]
    [InlineData(" 129.00")]
    [InlineData("129.00 ")]
    [InlineData("+129")]
    [InlineData("1e3")]
    [InlineData("129,00")]
    [InlineData("1,490.00")]
    [InlineData("1.490,00")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("١٢٣")]
    [InlineData("12٣")]
    [InlineData("1.٥")]
    public void Rejected_amounts(string? text) => Assert.False(Money.TryParseAmount(text, out _));

    [Fact]
    public void Minor_unit_rule_by_scale()
    {
        Assert.True(Money.TryParseAmount("1200.0", out var jpy));
        Assert.False(Money.HasAtMostFractionDigits(jpy, 0));
        Assert.True(Money.TryParseAmount("1200", out var whole));
        Assert.True(Money.HasAtMostFractionDigits(whole, 0));
        Assert.True(Money.TryParseAmount("99.955", out var dkk));
        Assert.False(Money.HasAtMostFractionDigits(dkk, 2));
        Assert.True(Money.TryParseAmount("1.234", out var kwd));
        Assert.True(Money.HasAtMostFractionDigits(kwd, 3));
        Assert.True(Money.TryParseAmount("1.2345".Substring(0, 5), out var kwd2));
        Assert.True(Money.HasAtMostFractionDigits(kwd2, 3));
    }

    [Fact]
    public void Currency_correction_is_judged_by_value()
    {
        Assert.True(Money.IsWholeMinorUnits(D("129.00"), 0));
        Assert.False(Money.IsWholeMinorUnits(D("129.50"), 0));
        Assert.True(Money.IsWholeMinorUnits(D("129.50"), 2));
        Assert.True(Money.IsWholeMinorUnits(D("1.234"), 3));
        Assert.False(Money.IsWholeMinorUnits(D("1.234"), 2));
        Assert.True(Money.IsWholeMinorUnits(0m, 0));
    }

    [Theory]
    [InlineData("129.00", 2, "129.00")]
    [InlineData("129", 2, "129.00")]
    [InlineData("129.00", 0, "129")]
    [InlineData("1.234", 3, "1.234")]
    [InlineData("0", 2, "0.00")]
    [InlineData("0.00", 0, "0")]
    [InlineData("999999999.99", 2, "999999999.99")]
    public void Format_amount_pads_or_drops_trailing_zeros_only(string value, int minor, string expected) =>
        Assert.Equal(expected, Money.FormatAmount(D(value), minor));

    [Fact]
    public void Format_amount_never_prints_negative_zero()
    {
        var negativeZero = -0.00m;
        Assert.Equal("0.00", Money.FormatAmount(negativeZero, 2));
        Assert.Equal("0", Money.FormatAmount(negativeZero, 0));
        Assert.Equal("-0.01", Money.FormatAmount(-0.01m, 2));
    }

    // ---- cadence --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("week", 1, true)]
    [InlineData("month", 1, true)]
    [InlineData("year", 99, true)]
    [InlineData("month", 0, false)]
    [InlineData("month", 100, false)]
    [InlineData("month", -1, false)]
    [InlineData("Month", 1, false)]
    [InlineData("day", 1, false)]
    [InlineData("", 1, false)]
    [InlineData(null, 1, false)]
    [InlineData("month", null, false)]
    public void Cadence_rules(string? unit, int? count, bool valid) => Assert.Equal(valid, Cadence.TryCreate(unit, count, out _));

    [Fact]
    public void Cadence_boundaries_0_1_99_100()
    {
        Assert.False(Cadence.IsValidCount(0));
        Assert.True(Cadence.IsValidCount(1));
        Assert.True(Cadence.IsValidCount(99));
        Assert.False(Cadence.IsValidCount(100));
    }

    // ---- fraction and rounding ------------------------------------------------------------------------

    [Fact]
    public void Fraction_from_decimal_is_exact_and_reduced()
    {
        var f = F("129.00");
        Assert.Equal(129, f.Numerator);
        Assert.Equal(1, f.Denominator);
        var g = F("0.125");
        Assert.Equal(1, g.Numerator);
        Assert.Equal(8, g.Denominator);
        Assert.Equal(-1, F("-0.125").Numerator);
        Assert.True(Fraction.Zero.IsZero);
        Assert.Equal(1, default(Fraction).Denominator);
    }

    [Fact]
    public void Fraction_arithmetic()
    {
        Assert.Equal(F("0.75"), F("0.5") + F("0.25"));
        Assert.Equal(F("0.25"), F("0.5") - F("0.25"));
        Assert.Equal(F("1.5"), F("0.5") * 3);
        Assert.Equal(F("0.25"), F("0.5") / 2);
        Assert.Equal(F("2"), F("1") / F("0.5"));
        Assert.Throws<DivideByZeroException>(() => F("1") / Fraction.Zero);
        Assert.Equal(-1, (F("1") - F("2")).Sign);
    }

    [Theory]
    [InlineData("month", 1, "129.00", "129.00", "1548.00")]   // EX-001
    [InlineData("year", 1, "1490.00", "124.17", "1490.00")]   // EX-002
    [InlineData("week", 1, "45.00", "195.00", "2340.00")]     // EX-003
    [InlineData("month", 3, "270.00", "90.00", "1080.00")]    // EX-004 LearnPro
    [InlineData("week", 2, "30.00", "65.00", "780.00")]       // EX-004 BoxKit
    [InlineData("month", 1, "11.99", "11.99", "143.88")]      // AC-010
    public void Normalization_examples(string unit, int count, string amount, string monthly, string yearly)
    {
        var cadence = new Cadence(unit, count);
        Assert.Equal(D(monthly), Money.RoundHalfAwayFromZero(cadence.Monthly(D(amount)), 2));
        Assert.Equal(D(yearly), Money.RoundHalfAwayFromZero(cadence.Yearly(D(amount)), 2));
    }

    [Fact]
    public void Totals_sum_unrounded_values_and_round_once_EX_006()
    {
        var each = new Cadence("year", 1).Monthly(D("1490.00"));
        Assert.Equal(124.17m, Money.RoundHalfAwayFromZero(each, 2));
        Assert.Equal(248.33m, Money.RoundHalfAwayFromZero(each + each, 2));
        Assert.NotEqual(248.34m, Money.RoundHalfAwayFromZero(each + each, 2));
        Assert.Equal(2980.00m, Money.RoundHalfAwayFromZero(new Cadence("year", 1).Yearly(D("1490.00")) * 2, 2));
    }

    [Fact]
    public void Exactness_guard_three_entries_at_4_plus_006_a_year_total_1_005_a_month_rounds_to_1_01()
    {
        var yearly = new Cadence("year", 1);
        var total = Fraction.Zero;
        foreach (var amount in new[] { "4.00", "4.00", "4.00", "0.06" })
        {
            total += yearly.Monthly(D(amount));
        }

        Assert.Equal(1.01m, Money.RoundHalfAwayFromZero(total, 2));
        Assert.Equal(1.005m, (decimal)total.Numerator / (decimal)total.Denominator);
        // Naive decimal division loses the exactness this test guards.
        Assert.Equal(1.00m, Math.Round(4.00m / 12 * 3 + 0.06m / 12, 2, MidpointRounding.AwayFromZero));
    }

    [Theory]
    [InlineData("1.00", "11.94", "-0.01")]   // monthly 1.00 then 0.995 a month
    [InlineData("1.00", "12.06", "0.01")]
    public void Half_cent_amount_changes_round_away_from_zero(string oldMonthly, string newYearly, string expected)
    {
        var change = new Cadence("year", 1).Monthly(D(newYearly)) - new Cadence("month", 1).Monthly(D(oldMonthly));
        Assert.Equal(D(expected), Money.RoundHalfAwayFromZero(change, 2));
    }

    [Theory]
    [InlineData("1199.40", "-0.1")]
    [InlineData("1200.60", "0.1")]
    public void Half_tenth_percent_changes_round_away_from_zero(string newYearly, string expected)
    {
        var oldMonthly = new Cadence("month", 1).Monthly(D("100.00"));
        var newMonthly = new Cadence("year", 1).Monthly(D(newYearly));
        Assert.Equal(D(expected), Money.RoundHalfAwayFromZero((newMonthly - oldMonthly) / oldMonthly * 100, 1));
    }

    [Fact]
    public void Zero_decimal_currencies_and_three_decimal_currencies_round_half_away()
    {
        Assert.Equal(1m, Money.RoundHalfAwayFromZero(new Cadence("year", 1).Monthly(D("6")), 0));      // JPY 6 a year
        Assert.Equal(124m, Money.RoundHalfAwayFromZero(new Cadence("year", 1).Monthly(D("1490")), 0)); // 124.1666…
        Assert.Equal(0.001m, Money.RoundHalfAwayFromZero(new Cadence("year", 1).Monthly(D("0.006")), 3));
        Assert.Equal(14.808m, Money.RoundHalfAwayFromZero(new Cadence("month", 1).Yearly(D("1.234")), 3));
    }

    [Fact]
    public void Negative_percentage_rounds_away_from_zero_EX_008()
    {
        var oldMonthly = new Cadence("month", 1).Monthly(D("99.00"));
        var newMonthly = new Cadence("year", 1).Monthly(D("999.00"));
        Assert.Equal(83.25m, Money.RoundHalfAwayFromZero(newMonthly, 2));
        Assert.Equal(-15.75m, Money.RoundHalfAwayFromZero(newMonthly - oldMonthly, 2));
        Assert.Equal(-15.9m, Money.RoundHalfAwayFromZero((newMonthly - oldMonthly) / oldMonthly * 100, 1));
    }

    [Fact]
    public void A_negative_change_that_rounds_to_zero_has_no_minus_sign()
    {
        var oldMonthly = new Cadence("month", 1).Monthly(D("100.00"));
        var newMonthly = new Cadence("year", 1).Monthly(D("1199.95"));
        var amount = Money.RoundHalfAwayFromZero(newMonthly - oldMonthly, 2);
        var percent = Money.RoundHalfAwayFromZero((newMonthly - oldMonthly) / oldMonthly * 100, 1);
        Assert.Equal("0.00", Money.FormatAmount(amount, 2));
        Assert.Equal("0.0", Money.FormatAmount(percent, 1));
        Assert.Equal(0m, amount);
    }

    [Fact]
    public void Rounding_a_value_larger_than_a_decimal_overflows_instead_of_wrapping()
    {
        var huge = new Fraction(System.Numerics.BigInteger.Pow(10, 40), 1);
        Assert.Throws<OverflowException>(() => Money.RoundHalfAwayFromZero(huge, 2));
    }

    [Fact]
    public void The_subscription_module_has_no_floating_point_members()
    {
        var assembly = typeof(Money).Assembly;
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
        foreach (var type in assembly.GetTypes().Where(t => t.Namespace?.StartsWith("Subscribed.Subscriptions", StringComparison.Ordinal) == true))
        {
            foreach (var field in type.GetFields(all))
            {
                Assert.DoesNotContain(field.FieldType, new[] { typeof(double), typeof(float) });
            }

            foreach (var property in type.GetProperties(all))
            {
                Assert.DoesNotContain(property.PropertyType, new[] { typeof(double), typeof(float) });
            }
        }
    }
}
