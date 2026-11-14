using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Subscribed.Platform.Http;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class HttpHelperTests
{
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
    };

    public sealed record Body(string? Amount, int? Count);

    [Theory]
    [InlineData(0L, "\"0\"")]
    [InlineData(3L, "\"3\"")]
    [InlineData(1234567890123L, "\"1234567890123\"")]
    public void Entity_tag_round_trips(long version, string text)
    {
        Assert.Equal(text, EntityTags.Format(version));
        Assert.True(EntityTags.TryParse(text, out var parsed));
        Assert.Equal(version, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("\"\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"1.5\"")]
    [InlineData("W/\"3\"")]
    [InlineData("\"abc\"")]
    public void Entity_tag_rejects_malformed_values(string? text) => Assert.False(EntityTags.TryParse(text, out _));

    [Fact]
    public async Task If_match_missing_is_428()
    {
        var context = NewContext();
        Assert.False(IfMatch.TryRead(context, out _, out var problem));
        var (status, json) = await RenderAsync(problem!, context);
        Assert.Equal(428, status);
        Assert.Equal("urn:subscribed:problem:if-match-required", json.GetProperty("type").GetString());
    }

    [Fact]
    public async Task If_match_malformed_is_400_keyed_if_match_in_camel_case()
    {
        var context = NewContext();
        context.Request.Headers.IfMatch = "3";
        Assert.False(IfMatch.TryRead(context, out _, out var problem));
        var (status, json) = await RenderAsync(problem!, context);
        Assert.Equal(400, status);
        Assert.True(json.GetProperty("errors").TryGetProperty("ifMatch", out _));
        Assert.False(json.GetProperty("errors").TryGetProperty("if-Match", out _));
    }

    [Fact]
    public void If_match_reads_the_version()
    {
        var context = NewContext();
        context.Request.Headers.IfMatch = "\"7\"";
        Assert.True(IfMatch.TryRead(context, out var version, out var problem));
        Assert.Equal(7, version);
        Assert.Null(problem);
    }

    [Fact]
    public async Task Validation_problem_carries_the_errors_as_given()
    {
        var context = NewContext();
        var (status, json) = await RenderAsync(ValidationProblems.Result(new Dictionary<string, string[]> { ["name"] = ["a", "b"], ["amount"] = ["c"] }), context);
        Assert.Equal(400, status);
        Assert.Equal("urn:subscribed:problem:validation-failed", json.GetProperty("type").GetString());
        Assert.Equal(2, json.GetProperty("errors").GetProperty("name").GetArrayLength());
        Assert.Equal(1, json.GetProperty("errors").GetProperty("amount").GetArrayLength());
    }

    [Fact]
    public async Task Json_request_reads_an_object()
    {
        var context = BodyContext("{\"amount\":\"1.00\",\"count\":2,\"extra\":true}");
        var result = await JsonRequest.ReadAsync<Body>(context, Strict);
        Assert.Null(result.Error);
        Assert.Equal(new Body("1.00", 2), result.Value);
    }

    [Fact]
    public async Task Json_request_matches_property_names_case_insensitively()
    {
        var result = await JsonRequest.ReadAsync<Body>(BodyContext("{\"AMOUNT\":\"1.00\"}"), Strict);
        Assert.Null(result.Error);
        Assert.Equal("1.00", result.Value!.Amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("{")]
    [InlineData("not json")]
    public async Task Json_request_rejects_bodies_that_are_not_objects_with_the_body_key(string text)
    {
        var context = BodyContext(text);
        var result = await JsonRequest.ReadAsync<Body>(context, Strict);
        Assert.Null(result.Value);
        var (status, json) = await RenderAsync(result.Error!, context);
        Assert.Equal(400, status);
        Assert.True(json.GetProperty("errors").TryGetProperty("body", out _));
    }

    [Theory]
    [InlineData("{\"amount\":12.5}", "amount")]
    [InlineData("{\"count\":\"1\"}", "count")]
    [InlineData("{\"count\":1.5}", "count")]
    [InlineData("{\"amount\":\"1\",\"count\":true}", "count")]
    [InlineData("{\"amount\":[1]}", "amount")]
    public async Task Json_request_keys_a_type_error_by_its_property(string text, string key)
    {
        var context = BodyContext(text);
        var result = await JsonRequest.ReadAsync<Body>(context, Strict);
        var (status, json) = await RenderAsync(result.Error!, context);
        Assert.Equal(400, status);
        var errors = json.GetProperty("errors");
        Assert.True(errors.TryGetProperty(key, out _));
        Assert.Single(errors.EnumerateObject());
    }

    [Fact]
    public async Task Json_request_requires_a_json_content_type()
    {
        var context = BodyContext("{}", "text/plain");
        var result = await JsonRequest.ReadAsync<Body>(context, Strict);
        var (status, _) = await RenderAsync(result.Error!, context);
        Assert.Equal(415, status);
    }

    [Fact]
    public async Task Json_request_rejects_a_declared_oversize_body_with_413()
    {
        var context = BodyContext("{}");
        context.Request.ContentLength = JsonRequest.MaxBodyBytes + 1;
        var result = await JsonRequest.ReadAsync<Body>(context, Strict);
        var (status, _) = await RenderAsync(result.Error!, context);
        Assert.Equal(413, status);
    }

    [Fact]
    public async Task Json_request_rejects_an_undeclared_oversize_body_with_413()
    {
        var context = BodyContext("{\"amount\":\"" + new string('a', JsonRequest.MaxBodyBytes) + "\"}");
        context.Request.ContentLength = null;
        var result = await JsonRequest.ReadAsync<Body>(context, Strict);
        var (status, _) = await RenderAsync(result.Error!, context);
        Assert.Equal(413, status);
    }

    [Theory]
    [InlineData("$.amount", "amount")]
    [InlineData("$.Amount", "amount")]
    [InlineData("$.items[0].x", "items")]
    [InlineData("$.a.b", "a")]
    [InlineData("$", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("$.", null)]
    public void First_property_of_a_json_path(string? path, string? expected) => Assert.Equal(expected, JsonRequest.FirstPropertyOf(path));

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext BodyContext(string text, string contentType = "application/json")
    {
        var context = NewContext();
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = contentType;
        context.Request.ContentLength = bytes.Length;
        return context;
    }

    private static async Task<(int Status, JsonElement Json)> RenderAsync(IResult result, DefaultHttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        return (context.Response.StatusCode, doc.RootElement.Clone());
    }
}
