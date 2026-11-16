using System.Text.Json;
using Subscribed.Subscriptions.Domain;
using Subscribed.Subscriptions.PaymentOptions;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class PaymentOptionEventTests
{
    [Theory]
    [InlineData(PaymentOptionEventTypes.Created, typeof(PaymentOptionCreated))]
    [InlineData(PaymentOptionEventTypes.Changed, typeof(PaymentOptionChanged))]
    [InlineData(PaymentOptionEventTypes.Removed, typeof(PaymentOptionRemoved))]
    public void Golden_fixtures_deserialize_and_round_trip(string alias, Type type)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "events", alias + ".json")).Trim();
        var e = PaymentOptionEventTypes.Deserialize(alias, json);
        Assert.IsType(type, e);
        Assert.Equal(alias, PaymentOptionEventTypes.AliasOf(type));

        // Every fixture property maps to a non-null CLR property, and every CLR property is in the fixture.
        var fixtureNames = JsonElement.Parse(json).EnumerateObject().Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(type.GetProperties().Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(), fixtureNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in fixtureNames)
        {
            Assert.NotNull(type.GetProperties().Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).GetValue(e));
        }

        Assert.Equal(e, PaymentOptionEventTypes.Deserialize(alias, PaymentOptionEventTypes.Serialize(e)));
    }

    [Fact]
    public void Aliases_follow_the_payment_option_pattern_and_stay_out_of_the_subscription_registry()
    {
        Assert.Equal(3, PaymentOptionEventTypes.ByAlias.Count);
        Assert.All(PaymentOptionEventTypes.ByAlias.Keys, a => Assert.Matches(@"^payment_option\.[a-z_]+\.v1$", a));
        Assert.DoesNotContain(SubscriptionEventTypes.ByAlias.Keys, a => a.StartsWith("payment_option.", StringComparison.Ordinal));
        foreach (var type in PaymentOptionEventTypes.ByAlias.Values)
        {
            Assert.Contains(type.GetProperties(), p => p.Name == "RecordedAt");
            Assert.DoesNotContain(type.GetProperties(), p => p.PropertyType == typeof(double) || p.PropertyType == typeof(float));
        }
    }

    [Fact]
    public void Unknown_alias_fails_loudly()
    {
        var ex = Assert.Throws<UnknownPaymentOptionEventAliasException>(() => PaymentOptionEventTypes.Deserialize("payment_option.bogus.v1", "{}"));
        Assert.Equal("payment_option.bogus.v1", ex.Alias);
        Assert.Throws<UnknownPaymentOptionEventAliasException>(() => PaymentOptionEventTypes.Resolve("payment_option.created.v2"));
    }
}

public sealed class PaymentOptionTextTests
{
    private static readonly string FullwidthSixteen = string.Concat(Enumerable.Repeat("４１１１", 4));
    private static readonly string ArabicIndicSixteen = string.Concat(Enumerable.Repeat("٤١١١", 4));

    [Theory]
    [InlineData("411111111111")]
    [InlineData("Visa ·· 4242")]
    [InlineData("Last 4 digits")]
    [InlineData("Joint account")]
    [InlineData("4111 1111 ab 1111 1111")]
    [InlineData("")]
    public void Texts_that_are_not_card_numbers_pass(string text)
    {
        Assert.False(PaymentOptionText.LooksLikeCardNumber(text));
        Assert.True(PaymentOptionText.TryNormalizeName(text.Length == 0 ? "x" : text, out _, out var nameReason), nameReason);
        Assert.True(PaymentOptionText.TryNormalizeLabel(text, out _, out var labelReason), labelReason);
    }

    [Theory]
    [InlineData("4111111111111")]
    [InlineData("4111111111111111")]
    [InlineData("4111111111111111111")]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData("4111.1111.1111.1111")]
    [InlineData("4111/1111/1111/1111")]
    [InlineData("4111_1111_1111_1111")]
    [InlineData("card 4111 1111 1111 1111 x")]
    public void Runs_of_thirteen_or_more_digits_are_rejected_in_name_and_label(string text)
    {
        Assert.True(PaymentOptionText.LooksLikeCardNumber(text));
        Assert.False(PaymentOptionText.TryNormalizeName(text, out _, out var nameReason));
        Assert.Equal(PaymentOptionText.ReasonCardNumberLike, nameReason);
        Assert.False(PaymentOptionText.TryNormalizeLabel(text, out _, out var labelReason));
        Assert.Equal(PaymentOptionText.ReasonCardNumberLike, labelReason);
    }

    [Fact]
    public void Any_unicode_decimal_digit_counts_and_nothing_throws()
    {
        Assert.True(PaymentOptionText.LooksLikeCardNumber(FullwidthSixteen));
        Assert.True(PaymentOptionText.LooksLikeCardNumber(ArabicIndicSixteen));
        Assert.True(PaymentOptionText.LooksLikeCardNumber("4111 ４１１１ 1111 ４１１１"));
        Assert.True(PaymentOptionText.LooksLikeCardNumber("4111 ٤١١١ 1111 １"));
        Assert.False(PaymentOptionText.TryNormalizeLabel(FullwidthSixteen, out _, out var reason));
        Assert.Equal(PaymentOptionText.ReasonCardNumberLike, reason);
        Assert.False(PaymentOptionText.LooksLikeCardNumber(null));
        Assert.False(PaymentOptionText.LooksLikeCardNumber("\ud800 lone surrogate 12"));
    }

    [Fact]
    public void A_letter_run_resets_the_digit_count()
    {
        Assert.False(PaymentOptionText.LooksLikeCardNumber("4111 1111 ab 1111 1111"));
        Assert.False(PaymentOptionText.LooksLikeCardNumber("123456789012a123456789012"));
    }

    [Fact]
    public void Name_and_label_limits_and_control_characters()
    {
        Assert.True(PaymentOptionText.TryNormalizeName("  Visa  ", out var name, out _));
        Assert.Equal("Visa", name);
        Assert.False(PaymentOptionText.TryNormalizeName("   ", out _, out var empty));
        Assert.Equal(PaymentOptionText.ReasonInvalidLength, empty);
        Assert.True(PaymentOptionText.TryNormalizeName(new string('a', 40), out _, out _));
        Assert.False(PaymentOptionText.TryNormalizeName(new string('a', 41), out _, out var longName));
        Assert.Equal(PaymentOptionText.ReasonInvalidLength, longName);
        Assert.False(PaymentOptionText.TryNormalizeName("Vi\tsa", out _, out var control));
        Assert.Equal(PaymentOptionText.ReasonControlCharacters, control);

        Assert.True(PaymentOptionText.TryNormalizeLabel("   ", out var none, out _));
        Assert.Null(none);
        Assert.True(PaymentOptionText.TryNormalizeLabel(null, out none, out _));
        Assert.Null(none);
        Assert.True(PaymentOptionText.TryNormalizeLabel(new string('a', 40), out _, out _));
        Assert.False(PaymentOptionText.TryNormalizeLabel(new string('a', 41), out _, out var longLabel));
        Assert.Equal(PaymentOptionText.ReasonInvalidLength, longLabel);
        Assert.False(PaymentOptionText.TryNormalizeLabel("a\nb", out _, out var labelControl));
        Assert.Equal(PaymentOptionText.ReasonControlCharacters, labelControl);
    }

    [Theory]
    [InlineData("name", "4111111111111111")]
    [InlineData("label", "4111 1111 1111 1111")]
    [InlineData("name", "x\u0001y")]
    public void Error_messages_never_echo_the_input(string field, string input)
    {
        var fields = field == "name" ? new PaymentOptionInput(input, null) : new PaymentOptionInput("Visa", input);
        var ex = Assert.Throws<PaymentOptionRejectedException>(() => PaymentOptionDecisions.ParseCreate(fields));
        Assert.Equal([field], ex.Errors.Keys);
        Assert.DoesNotContain(input, string.Join(' ', ex.Errors.Values.SelectMany(v => v)), StringComparison.Ordinal);
        Assert.DoesNotContain(input, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(input, string.Join(' ', ex.Reasons), StringComparison.Ordinal);
    }
}

public sealed class PaymentOptionDecisionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);

    private static List<object> Created(string name = "Visa", string? label = null) =>
        [.. PaymentOptionDecisions.Create(PaymentOptionDecisions.ParseCreate(new PaymentOptionInput(name, label)), "household-1", T0)];

    [Fact]
    public void Create_emits_one_event_that_rehydrates_to_the_option()
    {
        var events = Created("  Visa  ", " Joint ");
        var created = Assert.IsType<PaymentOptionCreated>(Assert.Single(events));
        Assert.Equal("household-1", created.HouseholdId);
        var state = PaymentOptionState.Rehydrate(events);
        Assert.Equal("Visa", state.Name);
        Assert.Equal("Joint", state.Label);
        Assert.False(state.Removed);
        Assert.Equal(1, state.Version);
        Assert.Equal(T0, state.CreatedAt);
    }

    [Fact]
    public void Change_renames_relabels_and_clears_the_label()
    {
        var history = Created("Visa", "Joint");
        var renamed = PaymentOptionDecisions.Change(history, new PaymentOptionFields("Visa Gold", "Joint"), T0.AddDays(1));
        Assert.IsType<PaymentOptionChanged>(Assert.Single(renamed));
        history.AddRange(renamed);
        var cleared = PaymentOptionDecisions.Change(history, new PaymentOptionFields("Visa Gold", null), T0.AddDays(2));
        history.AddRange(cleared);
        var state = PaymentOptionState.Rehydrate(history);
        Assert.Equal("Visa Gold", state.Name);
        Assert.Null(state.Label);
        Assert.Equal(3, state.Version);
    }

    [Fact]
    public void Change_with_nothing_changed_emits_no_event()
    {
        Assert.Empty(PaymentOptionDecisions.Change(Created("Visa", "Joint"), new PaymentOptionFields("Visa", "Joint"), T0));
    }

    [Fact]
    public void Remove_is_a_soft_remove_and_a_second_remove_is_not_found()
    {
        var history = Created();
        history.AddRange(PaymentOptionDecisions.Remove(history, T0.AddDays(1)));
        var state = PaymentOptionState.Rehydrate(history);
        Assert.True(state.Removed);
        Assert.Equal(T0.AddDays(1), state.RemovedAt);
        Assert.Equal("Visa", state.Name);
        Assert.Throws<PaymentOptionNotFoundException>(() => PaymentOptionDecisions.Remove(history, T0.AddDays(2)));
    }

    [Fact]
    public void Change_on_a_removed_option_is_not_found()
    {
        var history = Created();
        history.AddRange(PaymentOptionDecisions.Remove(history, T0.AddDays(1)));
        Assert.Throws<PaymentOptionNotFoundException>(() => PaymentOptionDecisions.Change(history, new PaymentOptionFields("Other", null), T0.AddDays(2)));
        Assert.Throws<PaymentOptionNotFoundException>(() => PaymentOptionDecisions.Change([], new PaymentOptionFields("Other", null), T0));
    }

    [Fact]
    public void Rehydrate_rejects_a_second_created_event_and_an_event_before_created()
    {
        var created = Created();
        Assert.Throws<InvalidOperationException>(() => PaymentOptionState.Rehydrate([.. created, .. created]));
        Assert.Throws<InvalidOperationException>(() => PaymentOptionState.Rehydrate([new PaymentOptionRemoved(T0)]));
        Assert.Throws<InvalidOperationException>(() => PaymentOptionState.Rehydrate([]));
    }

    [Fact]
    public void Ids_use_the_subscription_format_with_their_own_stream_prefix()
    {
        var id = PaymentOptionIds.New(TimeProvider.System);
        Assert.True(PaymentOptionIds.TryParse(id, out _));
        Assert.Equal("paymentoption-" + id, PaymentOptionIds.StreamKey(id));
        Assert.Equal(id, PaymentOptionIds.Bare(PaymentOptionIds.StreamKey(id)));
        Assert.False(PaymentOptionIds.TryParse("nope", out _));
    }

    [Fact]
    public void Suggestions_are_the_five_known_options_with_a_single_mobilepay_vipps()
    {
        Assert.Equal(["paypal", "visa", "mastercard", "dankort", "mobilepay-vipps"], PaymentOptionSuggestions.All.Select(s => s.Id));
        Assert.Single(PaymentOptionSuggestions.All, s => s.Name.Contains("MobilePay", StringComparison.Ordinal));
    }
}
