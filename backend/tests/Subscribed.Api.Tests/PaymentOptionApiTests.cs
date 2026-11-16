using System.Net;
using System.Text.Json;
using Marten;
using Subscribed.Platform.EventStore;
using Subscribed.Subscriptions.PaymentOptions;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

public sealed class PaymentOptionApiTests(PostgresFixture pg)
{
    private const string Options = "/api/payment-options";
    private const string Subs = "/api/subscriptions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<ApiTestHost> MemberHostAsync() => new SubscriptionApiTests(pg).MemberHostAsync();

    // ---- helpers ---------------------------------------------------------------------------------

    private static async Task<JsonElement> CreateOptionAsync(ApiTestHost host, string name, string? label = null)
    {
        using var response = await host.SendAsync(HttpMethod.Post, Options, new { name, label });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var json = await ApiTestHost.JsonAsync(response);
        Assert.Equal($"{Options}/{json.GetProperty("id").GetString()}", response.Headers.Location?.ToString());
        Assert.Equal($"\"{json.GetProperty("version").GetInt64()}\"", response.Headers.ETag?.ToString());
        return json;
    }

    private static string IdOf(JsonElement element) => element.GetProperty("id").GetString()!;

    private static string Tag(JsonElement element) => $"\"{element.GetProperty("version").GetInt64()}\"";

    private static async Task<JsonElement> ListAsync(ApiTestHost host)
    {
        using var response = await host.SendAsync(HttpMethod.Get, Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.JsonAsync(response)).GetProperty("options");
    }

    private static async Task<JsonElement> RenameAsync(ApiTestHost host, JsonElement option, string name, string? label = null)
    {
        using var response = await host.SendAsync(HttpMethod.Put, $"{Options}/{IdOf(option)}", new { name, label }, Tag(option));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await ApiTestHost.JsonAsync(response);
    }

    private static async Task RemoveOptionAsync(ApiTestHost host, JsonElement option)
    {
        using var response = await host.SendAsync(HttpMethod.Delete, $"{Options}/{IdOf(option)}", null, Tag(option));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static object NewSubscription(ApiTestHost host, string? paymentOptionId, string name = "StreamCo") => new
    {
        name,
        category = (string?)null,
        notes = (string?)null,
        currency = "DKK",
        startDate = SubscriptionApiTests.Day(host, -100),
        endDate = (string?)null,
        amount = "129.00",
        cadenceUnit = "month",
        cadenceCount = 1,
        paymentOptionId,
    };

    private static object PriceBody(ApiTestHost host, int day, string amount, string? paymentOptionId) => new
    {
        effectiveFrom = SubscriptionApiTests.Day(host, day),
        amount,
        cadenceUnit = "month",
        cadenceCount = 1,
        paymentOptionId,
    };

    private static string? OptionOf(JsonElement row) => row.GetProperty("paymentOptionId").GetString();

    private static async Task<string> StartForeignOptionAsync(ApiTestHost host)
    {
        await using var store = SubscribedStore.Create(host.Db.AppConnectionString, PostgresFixture.Schema, StoreRole.App, PostgresFixture.Modules);
        var id = PaymentOptionIds.New(TimeProvider.System);
        await using var session = store.LightweightSession();
        session.Events.StartStream<PaymentOptionState>(PaymentOptionIds.StreamKey(id), new PaymentOptionCreated("household-other", "Foreign", null, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(Ct);
        return id;
    }

    // ---- suggestions and CRUD ------------------------------------------------------------------------

    [Fact]
    public async Task Suggestions_are_exactly_the_five_known_options_with_one_mobilepay_vipps()
    {
        await using var host = await MemberHostAsync();
        using var response = await host.SendAsync(HttpMethod.Get, Options + "/suggestions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var suggestions = (await ApiTestHost.JsonAsync(response)).GetProperty("suggestions");
        Assert.Equal(["paypal", "visa", "mastercard", "dankort", "mobilepay-vipps"], suggestions.EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Single(suggestions.EnumerateArray(), s => s.GetProperty("name").GetString()!.Contains("MobilePay", StringComparison.Ordinal));
        Assert.All(suggestions.EnumerateArray(), s => Assert.Equal(["id", "name"], s.EnumerateObject().Select(p => p.Name)));
    }

    [Fact]
    public async Task An_option_is_created_listed_renamed_relabelled_and_soft_removed()
    {
        await using var host = await MemberHostAsync();
        Assert.Equal(0, (await ListAsync(host)).GetArrayLength());

        var visa = await CreateOptionAsync(host, "  Visa ", " Joint ");
        Assert.Equal("Visa", visa.GetProperty("name").GetString());
        Assert.Equal("Joint", visa.GetProperty("label").GetString());
        Assert.False(visa.GetProperty("removed").GetBoolean());
        Assert.Equal(1, visa.GetProperty("version").GetInt64());
        var custom = await CreateOptionAsync(host, "Company card");
        Assert.Equal(JsonValueKind.Null, custom.GetProperty("label").ValueKind);

        var renamed = await RenameAsync(host, visa, "Visa Gold", "Private");
        Assert.Equal(2, renamed.GetProperty("version").GetInt64());
        Assert.Equal("Private", renamed.GetProperty("label").GetString());
        var cleared = await RenameAsync(host, renamed, "Visa Gold", null);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("label").ValueKind);

        using (var read = await host.SendAsync(HttpMethod.Get, $"{Options}/{IdOf(visa)}"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(3, (await ApiTestHost.JsonAsync(read)).GetProperty("version").GetInt64());
        }

        // A no-op update appends nothing.
        var unchanged = await RenameAsync(host, cleared, "Visa Gold", null);
        Assert.Equal(3, unchanged.GetProperty("version").GetInt64());

        await RemoveOptionAsync(host, unchanged);
        var listed = await ListAsync(host);
        Assert.Equal(2, listed.GetArrayLength());
        Assert.Equal(["Company card", "Visa Gold"], listed.EnumerateArray().Select(o => o.GetProperty("name").GetString()));
        Assert.Equal([false, true], listed.EnumerateArray().Select(o => o.GetProperty("removed").GetBoolean()));
    }

    [Fact]
    public async Task Writes_check_id_if_match_body_shape_and_version_in_that_order()
    {
        await using var host = await MemberHostAsync();
        var option = await CreateOptionAsync(host, "Visa");
        var id = IdOf(option);
        var missing = new string('0', 32);

        using (var badId = await host.SendAsync(HttpMethod.Put, $"{Options}/not-an-id", new { name = "x" }, "\"1\""))
        {
            await ApiTestHost.AssertProblemAsync(badId, HttpStatusCode.NotFound, "not-found");
        }

        using (var noMatch = await host.SendAsync(HttpMethod.Put, $"{Options}/{id}", new { name = "x" }))
        {
            await ApiTestHost.AssertProblemAsync(noMatch, HttpStatusCode.PreconditionRequired, "if-match-required");
        }

        using (var badMatch = await host.SendAsync(HttpMethod.Put, $"{Options}/{id}", new { name = "x" }, "1"))
        {
            await ApiTestHost.AssertProblemAsync(badMatch, HttpStatusCode.BadRequest, "validation-failed");
        }

        using (var invalid = await host.SendAsync(HttpMethod.Put, $"{Options}/{missing}", new { name = "" }, "\"1\""))
        {
            await ApiTestHost.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation-failed");
        }

        using (var unknown = await host.SendAsync(HttpMethod.Put, $"{Options}/{missing}", new { name = "Fine" }, "\"1\""))
        {
            await ApiTestHost.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "not-found");
        }

        using (var stale = await host.SendAsync(HttpMethod.Put, $"{Options}/{id}", new { name = "Fine" }, "\"7\""))
        {
            await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
            Assert.Equal(1, (await ApiTestHost.JsonAsync(stale)).GetProperty("currentVersion").GetInt64());
        }

        using (var staleDelete = await host.SendAsync(HttpMethod.Delete, $"{Options}/{id}", null, "\"7\""))
        {
            await ApiTestHost.AssertProblemAsync(staleDelete, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        }

        using var noBody = await host.SendAsync(HttpMethod.Post, Options, rawBody: "[]");
        await ApiTestHost.AssertProblemAsync(noBody, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task Put_and_delete_on_a_removed_option_respond_404()
    {
        await using var host = await MemberHostAsync();
        var option = await CreateOptionAsync(host, "Visa");
        await RemoveOptionAsync(host, option);
        var removedTag = "\"2\"";

        using (var put = await host.SendAsync(HttpMethod.Put, $"{Options}/{IdOf(option)}", new { name = "Back" }, removedTag))
        {
            await ApiTestHost.AssertProblemAsync(put, HttpStatusCode.NotFound, "not-found");
        }

        using var delete = await host.SendAsync(HttpMethod.Delete, $"{Options}/{IdOf(option)}", null, removedTag);
        await ApiTestHost.AssertProblemAsync(delete, HttpStatusCode.NotFound, "not-found");
    }

    // ---- card-number rule ------------------------------------------------------------------------

    [Theory]
    [InlineData("name", "4111 1111 1111 1111")]
    [InlineData("label", "4111-1111-1111-1111")]
    [InlineData("label", "４１１１１１１１１１１１１１１１")]
    [InlineData("name", "4111_1111_1111_1111")]
    public async Task A_card_number_like_name_or_label_is_a_400_and_is_never_echoed(string field, string value)
    {
        await using var host = await MemberHostAsync();
        object body = field == "name" ? new { name = value, label = (string?)null } : new { name = "Visa", label = (string?)value };
        using var response = await host.SendAsync(HttpMethod.Post, Options, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        Assert.DoesNotContain("1111", text, StringComparison.Ordinal);
        Assert.Contains($"\"{field}\"", text, StringComparison.Ordinal);
        Assert.Equal(0, (await ListAsync(host)).GetArrayLength());

        var option = await CreateOptionAsync(host, "Visa");
        object update = field == "name" ? new { name = value, label = (string?)null } : new { name = "Visa", label = (string?)value };
        using var rejected = await host.SendAsync(HttpMethod.Put, $"{Options}/{IdOf(option)}", update, Tag(option));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.DoesNotContain(value, await rejected.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Twelve_digits_and_phrases_with_a_few_digits_are_accepted()
    {
        await using var host = await MemberHostAsync();
        await CreateOptionAsync(host, "Visa ·· 4242", "Last 4 digits");
        await CreateOptionAsync(host, "Reference", "411111111111");
    }

    // ---- assignment to price entries -----------------------------------------------------------

    [Fact]
    public async Task Assignments_over_time_stay_visible_in_history_and_current_price()
    {
        await using var host = await MemberHostAsync();
        var a = await CreateOptionAsync(host, "Visa");
        var b = await CreateOptionAsync(host, "PayPal");
        var created = await SubscriptionApiTests.AddAsync(host, NewSubscription(host, IdOf(a)));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal(IdOf(a), OptionOf(created.GetProperty("currentPrice")));
        Assert.Equal(IdOf(a), OptionOf(created.GetProperty("priceHistory")[0]));
        Assert.Equal(2, created.GetProperty("version").GetInt64());

        var detail = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Post, $"{Subs}/{id}/prices", PriceBody(host, -50, "149.00", IdOf(b)), SubscriptionApiTests.ETag(created));
        Assert.Equal(IdOf(b), OptionOf(detail.GetProperty("currentPrice")));
        Assert.Equal([IdOf(a), IdOf(b)], detail.GetProperty("priceHistory").EnumerateArray().Select(OptionOf));

        var overview = await SubscriptionApiTests.OverviewAsync(host);
        Assert.Equal(IdOf(b), OptionOf(overview.GetProperty("subscriptions")[0].GetProperty("currentPrice")));

        // Entries added without an option have none, and the key is still present.
        var plain = await SubscriptionApiTests.AddAsync(host, NewSubscription(host, null, "Plain"));
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("currentPrice").GetProperty("paymentOptionId").ValueKind);
    }

    [Fact]
    public async Task Correcting_with_price_and_option_together_raises_the_version_by_two_and_a_price_only_correction_with_the_key_keeps_it()
    {
        await using var host = await MemberHostAsync();
        var a = await CreateOptionAsync(host, "Visa");
        var created = await SubscriptionApiTests.AddAsync(host, NewSubscription(host, null));
        var id = created.GetProperty("id").GetString()!;
        var entryId = created.GetProperty("priceHistory")[0].GetProperty("entryId").GetString()!;
        var version = created.GetProperty("version").GetInt64();

        var both = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Subs}/{id}/prices/{entryId}", new
        {
            effectiveFrom = SubscriptionApiTests.Day(host, -100),
            amount = "139.00",
            cadenceUnit = "month",
            cadenceCount = 1,
            paymentOptionId = IdOf(a),
        }, SubscriptionApiTests.ETag(created));
        Assert.Equal(version + 2, both.GetProperty("version").GetInt64());
        Assert.Equal($"\"{version + 2}\"", $"\"{both.GetProperty("version").GetInt64()}\"");
        Assert.Equal(IdOf(a), OptionOf(both.GetProperty("currentPrice")));

        var priceOnly = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Subs}/{id}/prices/{entryId}", new
        {
            effectiveFrom = SubscriptionApiTests.Day(host, -100),
            amount = "149.00",
            cadenceUnit = "month",
            cadenceCount = 1,
            paymentOptionId = IdOf(a),
        }, SubscriptionApiTests.ETag(both));
        Assert.Equal(version + 3, priceOnly.GetProperty("version").GetInt64());
        Assert.Equal(IdOf(a), OptionOf(priceOnly.GetProperty("currentPrice")));

        // Omitting the key is full-replace semantics: it clears the link with one event (D9).
        var cleared = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Subs}/{id}/prices/{entryId}", new
        {
            effectiveFrom = SubscriptionApiTests.Day(host, -100),
            amount = "149.00",
            cadenceUnit = "month",
            cadenceCount = 1,
        }, SubscriptionApiTests.ETag(priceOnly));
        Assert.Equal(version + 4, cleared.GetProperty("version").GetInt64());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("currentPrice").GetProperty("paymentOptionId").ValueKind);
    }

    [Fact]
    public async Task The_details_put_neither_sets_nor_clears_the_option()
    {
        await using var host = await MemberHostAsync();
        var a = await CreateOptionAsync(host, "Visa");
        var created = await SubscriptionApiTests.AddAsync(host, NewSubscription(host, IdOf(a)));
        var id = created.GetProperty("id").GetString()!;

        var updated = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Subs}/{id}", SubscriptionApiTests.Put(created, name: "Renamed"), SubscriptionApiTests.ETag(created));
        Assert.Equal("Renamed", updated.GetProperty("name").GetString());
        Assert.Equal(IdOf(a), OptionOf(updated.GetProperty("currentPrice")));

        // A paymentOptionId in the details body is ignored, not applied.
        var other = await CreateOptionAsync(host, "PayPal");
        var body = JsonSerializer.SerializeToElement(SubscriptionApiTests.Put(updated, name: "Renamed again"));
        var withOption = body.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        withOption["paymentOptionId"] = IdOf(other);
        var ignored = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Subs}/{id}", withOption, SubscriptionApiTests.ETag(updated));
        Assert.Equal(IdOf(a), OptionOf(ignored.GetProperty("currentPrice")));
    }

    [Fact]
    public async Task Assigning_an_unknown_removed_or_malformed_option_is_a_400_on_payment_option_id()
    {
        await using var host = await MemberHostAsync();
        var gone = await CreateOptionAsync(host, "Gone");
        await RemoveOptionAsync(host, gone);
        var created = await SubscriptionApiTests.AddAsync(host, NewSubscription(host, null));
        var id = created.GetProperty("id").GetString()!;

        foreach (var bad in new[] { new string('0', 32), IdOf(gone), "not-an-id" })
        {
            using var add = await host.SendAsync(HttpMethod.Post, $"{Subs}/{id}/prices", PriceBody(host, -50, "149.00", bad), SubscriptionApiTests.ETag(created));
            Assert.Equal(["paymentOptionId"], (await SubscriptionApiTests.ErrorsAsync(add)).EnumerateObject().Select(p => p.Name));
        }

        Assert.Equal(created.GetProperty("version").GetInt64(), (await SubscriptionApiTests.GetAsync(host, id)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Adding_a_subscription_with_an_unknown_removed_or_foreign_option_is_a_400_and_creates_no_stream()
    {
        await using var host = await MemberHostAsync();
        var gone = await CreateOptionAsync(host, "Gone");
        await RemoveOptionAsync(host, gone);
        var foreign = await StartForeignOptionAsync(host);

        foreach (var bad in new[] { new string('0', 32), IdOf(gone), foreign })
        {
            using var response = await host.SendAsync(HttpMethod.Post, Subs, NewSubscription(host, bad));
            Assert.Equal(["paymentOptionId"], (await SubscriptionApiTests.ErrorsAsync(response)).EnumerateObject().Select(p => p.Name));
        }

        Assert.Equal(0, (await SubscriptionApiTests.OverviewAsync(host)).GetProperty("subscriptions").GetArrayLength());
        Assert.Equal(0, await host.Db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM subscribed.mt_streams WHERE id LIKE 'subscription-%'"));

        var good = await CreateOptionAsync(host, "Visa");
        var created = await SubscriptionApiTests.AddAsync(host, NewSubscription(host, IdOf(good)));
        Assert.Equal(IdOf(good), OptionOf((await SubscriptionApiTests.GetAsync(host, created.GetProperty("id").GetString()!)).GetProperty("currentPrice")));
    }

    // ---- removal, rename, isolation, race --------------------------------------------------------

    [Fact]
    public async Task Removing_an_option_keeps_history_intact_and_still_listed_as_removed()
    {
        await using var host = await MemberHostAsync();
        var a = await CreateOptionAsync(host, "Visa");
        var created = await SubscriptionApiTests.AddAsync(host, NewSubscription(host, IdOf(a)));
        var id = created.GetProperty("id").GetString()!;
        await RemoveOptionAsync(host, a);

        var detail = await SubscriptionApiTests.GetAsync(host, id);
        Assert.Equal(IdOf(a), OptionOf(detail.GetProperty("currentPrice")));
        Assert.Equal(IdOf(a), OptionOf(detail.GetProperty("priceHistory")[0]));
        var listed = Assert.Single((await ListAsync(host)).EnumerateArray());
        Assert.Equal(IdOf(a), IdOf(listed));
        Assert.True(listed.GetProperty("removed").GetBoolean());
        Assert.Equal("Visa", listed.GetProperty("name").GetString());

        // The entry is still editable with its removed option unchanged (D10).
        var entryId = detail.GetProperty("priceHistory")[0].GetProperty("entryId").GetString()!;
        var edited = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Subs}/{id}/prices/{entryId}", new
        {
            effectiveFrom = SubscriptionApiTests.Day(host, -100),
            amount = "139.00",
            cadenceUnit = "month",
            cadenceCount = 1,
            paymentOptionId = IdOf(a),
        }, SubscriptionApiTests.ETag(detail));
        Assert.Equal(IdOf(a), OptionOf(edited.GetProperty("currentPrice")));
    }

    [Fact]
    public async Task Renaming_an_option_changes_how_old_history_rows_resolve_while_their_id_stays()
    {
        await using var host = await MemberHostAsync();
        var a = await CreateOptionAsync(host, "Visa");
        var created = await SubscriptionApiTests.AddAsync(host, NewSubscription(host, IdOf(a)));
        var renamed = await RenameAsync(host, a, "Visa Platinum", "Joint");

        var detail = await SubscriptionApiTests.GetAsync(host, created.GetProperty("id").GetString()!);
        Assert.Equal(IdOf(a), OptionOf(detail.GetProperty("priceHistory")[0]));
        var listed = Assert.Single((await ListAsync(host)).EnumerateArray());
        Assert.Equal(IdOf(renamed), IdOf(listed));
        Assert.Equal("Visa Platinum", listed.GetProperty("name").GetString());
        Assert.Equal("Joint", listed.GetProperty("label").GetString());
    }

    [Fact]
    public async Task An_option_of_another_household_is_a_404_to_read_update_and_remove_and_a_400_to_assign()
    {
        await using var host = await MemberHostAsync();
        var foreign = await StartForeignOptionAsync(host);

        Assert.Equal(0, (await ListAsync(host)).GetArrayLength());
        using (var read = await host.SendAsync(HttpMethod.Get, $"{Options}/{foreign}"))
        {
            await ApiTestHost.AssertProblemAsync(read, HttpStatusCode.NotFound, "not-found");
        }

        using (var update = await host.SendAsync(HttpMethod.Put, $"{Options}/{foreign}", new { name = "Mine now" }, "\"1\""))
        {
            await ApiTestHost.AssertProblemAsync(update, HttpStatusCode.NotFound, "not-found");
        }

        using (var remove = await host.SendAsync(HttpMethod.Delete, $"{Options}/{foreign}", null, "\"1\""))
        {
            await ApiTestHost.AssertProblemAsync(remove, HttpStatusCode.NotFound, "not-found");
        }

        var created = await SubscriptionApiTests.AddAsync(host, NewSubscription(host, null));
        using var assign = await host.SendAsync(HttpMethod.Post, $"{Subs}/{created.GetProperty("id").GetString()}/prices", PriceBody(host, -50, "149.00", foreign), SubscriptionApiTests.ETag(created));
        Assert.Equal(["paymentOptionId"], (await SubscriptionApiTests.ErrorsAsync(assign)).EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Race_read_back_assign_remove_then_read_still_shows_the_id_resolved_as_removed()
    {
        await using var host = await MemberHostAsync();
        var a = await CreateOptionAsync(host, "Visa");
        var created = await SubscriptionApiTests.AddAsync(host, NewSubscription(host, null));
        var id = created.GetProperty("id").GetString()!;
        var assigned = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Post, $"{Subs}/{id}/prices", PriceBody(host, -50, "149.00", IdOf(a)), SubscriptionApiTests.ETag(created));
        await RemoveOptionAsync(host, a);

        var detail = await SubscriptionApiTests.GetAsync(host, id);
        Assert.Equal(assigned.GetProperty("version").GetInt64(), detail.GetProperty("version").GetInt64());
        Assert.Equal(IdOf(a), OptionOf(detail.GetProperty("currentPrice")));
        Assert.True(Assert.Single((await ListAsync(host)).EnumerateArray()).GetProperty("removed").GetBoolean());
    }

    // ---- contract --------------------------------------------------------------------------------

    [Fact]
    public async Task The_option_list_and_suggestions_match_the_golden_files_the_frontend_specs_use()
    {
        await using var host = await MemberHostAsync();
        var visa = await CreateOptionAsync(host, "Visa", "Joint account");
        var old = await CreateOptionAsync(host, "Old card");
        await RemoveOptionAsync(host, old);

        using var list = await host.SendAsync(HttpMethod.Get, Options);
        SubscriptionContractTests.Compare("payment-options.json", await ApiTestHost.JsonAsync(list));
        using var suggestions = await host.SendAsync(HttpMethod.Get, Options + "/suggestions");
        SubscriptionContractTests.Compare("payment-option-suggestions.json", await ApiTestHost.JsonAsync(suggestions));
        Assert.NotEqual(IdOf(visa), IdOf(old));
    }

    // ---- logging and observability (NFR-2) --------------------------------------------------------

    [Fact]
    public async Task Logs_never_contain_the_name_or_label_including_the_rejected_and_the_exception_paths()
    {
        await using var host = await MemberHostAsync();
        const string labelSentinel = "zz-label-7f3a91c2";
        const string nameSentinel = "zz-name-5d1e08b4";

        var created = await CreateOptionAsync(host, nameSentinel, labelSentinel);
        await RenameAsync(host, created, nameSentinel + "-2", labelSentinel + "-2");

        // Rejected by the card-number rule, while carrying a sentinel in the other field.
        using (var rejected = await host.SendAsync(HttpMethod.Post, Options, new { name = nameSentinel + "-3", label = "4111 1111 1111 1111" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        // Rejected while reading the body: a number where text is expected makes the deserializer throw.
        using (var broken = await host.SendAsync(HttpMethod.Post, Options, rawBody: $$"""{"name":"{{nameSentinel}}-4","label":1234567890123456}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        }

        using (var truncated = await host.SendAsync(HttpMethod.Post, Options, rawBody: $$"""{"name":"{{nameSentinel}}-5","label":"{{labelSentinel}}-5"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, truncated.StatusCode);
        }

        // Positive controls: the capture sees these requests, so an empty result below is not a silent pass.
        Assert.Contains(host.Logs.Entries, e => e.Contains(Options, StringComparison.Ordinal));
        Assert.Contains(host.Logs.Entries, e => e.Contains("payment_option.created", StringComparison.Ordinal));
        Assert.Contains(host.Logs.Entries, e => e.Contains("payment_option.rejected card_number_like", StringComparison.Ordinal));
        Assert.Empty(host.Logs.Find(labelSentinel, nameSentinel, "zz-label", "zz-name", "4111 1111"));
    }

    [Fact]
    public async Task Each_rejection_reason_each_creation_and_each_removal_logs_a_content_free_signal()
    {
        await using var host = await MemberHostAsync();
        using (var card = await host.SendAsync(HttpMethod.Post, Options, new { name = "Visa", label = "4111111111111111" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, card.StatusCode);
        }

        using (var length = await host.SendAsync(HttpMethod.Post, Options, new { name = new string('n', 41), label = (string?)null }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, length.StatusCode);
        }

        using (var control = await host.SendAsync(HttpMethod.Post, Options, new { name = "Vi\u0001sa", label = (string?)null }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, control.StatusCode);
        }

        var option = await CreateOptionAsync(host, "Visa", "Joint");
        await RemoveOptionAsync(host, option);

        string[] SignalsOf(string signal) => [.. host.Logs.Entries.Where(e => e.Contains(signal, StringComparison.Ordinal))];
        Assert.Single(SignalsOf("payment_option.rejected card_number_like"));
        Assert.Single(SignalsOf("payment_option.rejected invalid_length"));
        Assert.Single(SignalsOf("payment_option.rejected control_characters"));
        Assert.Equal(3, SignalsOf("payment_option.rejected").Length);

        var created = Assert.Single(SignalsOf("payment_option.created"));
        Assert.Contains(IdOf(option), created, StringComparison.Ordinal);
        var removed = Assert.Single(SignalsOf("payment_option.removed"));
        Assert.Contains(IdOf(option), removed, StringComparison.Ordinal);
        foreach (var entry in SignalsOf("payment_option."))
        {
            Assert.DoesNotContain("Visa", entry, StringComparison.Ordinal);
            Assert.DoesNotContain("Joint", entry, StringComparison.Ordinal);
            Assert.DoesNotContain("nnnnnn", entry, StringComparison.Ordinal);
        }
    }

    // ---- guards ----------------------------------------------------------------------------------

    private static IEnumerable<(HttpMethod Method, string Path, bool Body)> AllRoutes(string id) =>
    [
        (HttpMethod.Get, Options, false),
        (HttpMethod.Get, Options + "/suggestions", false),
        (HttpMethod.Get, $"{Options}/{id}", false),
        (HttpMethod.Post, Options, true),
        (HttpMethod.Put, $"{Options}/{id}", true),
        (HttpMethod.Delete, $"{Options}/{id}", false),
    ];

    [Fact]
    public async Task Anonymous_gets_401_and_non_members_get_403_on_every_route()
    {
        await using var host = await MemberHostAsync();
        var option = await CreateOptionAsync(host, "Secret card");
        var id = IdOf(option);

        await SubscriptionApiTests.SwitchUserAsync(host, "subject-stranger");
        foreach (var target in new[] { id, new string('0', 32) })
        {
            foreach (var (method, path, body) in AllRoutes(target))
            {
                using var response = await host.SendAsync(method, path, body ? new { } : null, "\"1\"");
                await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Forbidden, "not-a-household-member");
                Assert.DoesNotContain("Secret", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            }
        }

        await using var anonymous = await ApiTestHost.StartAsync(pg);
        await anonymous.InitHouseholdAsync("subject-owner");
        foreach (var (method, path, body) in AllRoutes(new string('a', 32)))
        {
            using var response = await anonymous.SendAsync(method, path, body ? new { } : null, "\"1\"");
            await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
        }
    }

    [Fact]
    public async Task Csrf_and_origin_are_enforced_on_every_unsafe_payment_option_route()
    {
        await using var host = await MemberHostAsync();
        var option = await CreateOptionAsync(host, "Visa");
        foreach (var (method, path, body) in AllRoutes(IdOf(option)).Where(r => r.Method != HttpMethod.Get))
        {
            using var noCsrf = await host.SendAsync(method, path, body ? new { } : null, "\"1\"", csrf: false);
            await ApiTestHost.AssertProblemAsync(noCsrf, HttpStatusCode.Forbidden, "csrf-header-required");
            using var wrongOrigin = await host.SendAsync(method, path, body ? new { } : null, "\"1\"", origin: "https://evil.example");
            await ApiTestHost.AssertProblemAsync(wrongOrigin, HttpStatusCode.Forbidden, "origin-mismatch");
        }
    }
}
