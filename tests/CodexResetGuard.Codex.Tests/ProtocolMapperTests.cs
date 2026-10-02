using System.Text.Json.Nodes;
using CodexResetGuard.Codex.AppServer;
using CodexResetGuard.Core.Domain;

namespace CodexResetGuard.Codex.Tests;

public class ProtocolMapperTests
{
    private static readonly DateTimeOffset ReadAt = DateTimeOffset.FromUnixTimeSeconds(1790930000);

    /// <summary>Shape recorded from codex-cli 0.159.3 (identifiers replaced).</summary>
    internal const string RecordedRateLimits = """
        {
          "ordinaryUsageAllowed": true,
          "rateLimits": {
            "limitId": "codex", "limitName": null, "normalModelSlug": null,
            "primary":   { "usedPercent": 5,  "windowDurationMins": 300,   "resetsAt": 1790947849 },
            "secondary": { "usedPercent": 41, "windowDurationMins": 10080, "resetsAt": 1791389735 },
            "credits": { "hasCredits": false, "unlimited": false, "balance": "0" },
            "individualLimit": null, "spendControlReached": false, "planType": "plus",
            "rateLimitReachedType": null
          },
          "rateLimitsByLimitId": {
            "codex": {
              "limitId": "codex",
              "primary":   { "usedPercent": 5,  "windowDurationMins": 300,   "resetsAt": 1790947849 },
              "secondary": { "usedPercent": 41, "windowDurationMins": 10080, "resetsAt": 1791389735 },
              "rateLimitReachedType": null
            }
          },
          "rateLimitResetCredits": {
            "availableCount": 2,
            "credits": [
              { "id": "credit-1", "resetType": "codexRateLimits", "status": "available",
                "grantedAt": 1790108480, "expiresAt": 1792700480,
                "title": "Full reset (Weekly + 5 hr)", "description": "…" },
              { "id": "credit-2", "resetType": "codexRateLimits", "status": "available",
                "grantedAt": 1790707413, "expiresAt": 1793299413,
                "title": "Full reset (Weekly + 5 hr)", "description": "…" }
            ]
          },
          "accountId": "acct-1",
          "rateLimitUpsell": null
        }
        """;

    [Fact]
    public void Maps_the_recorded_response()
    {
        var usage = ProtocolMapper.MapUsage(JsonNode.Parse(RecordedRateLimits), ReadAt);

        Assert.True(usage.UsageAllowed);
        Assert.Equal("acct-1", usage.AccountId);
        Assert.Equal(5, usage.FiveHour!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790947849), usage.FiveHour.ResetsAt);
        Assert.Equal(41, usage.Weekly!.UsedPercent);
        Assert.Equal(10080, usage.Weekly.WindowDurationMinutes);
        Assert.Equal(2, usage.AvailableResetCount);
        Assert.True(usage.ResetCreditsReported);
        Assert.Equal(["credit-1", "credit-2"], usage.Credits!.Select(c => c.Id));
        Assert.All(usage.Credits!, c => Assert.Equal(ResetCreditStatus.Available, c.Status));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792700480), usage.Credits![0].ExpiresAt);
        Assert.Equal(ReadAt, usage.ReadAt);
    }

    [Fact]
    public void Windows_are_matched_by_duration_not_position()
    {
        var json = """
            {
              "ordinaryUsageAllowed": false,
              "rateLimits": {
                "primary":   { "usedPercent": 100, "windowDurationMins": 10080, "resetsAt": 1791389735 },
                "secondary": { "usedPercent": 20,  "windowDurationMins": 300,   "resetsAt": 1790947849 },
                "rateLimitReachedType": "rate_limit_reached"
              },
              "rateLimitsByLimitId": null,
              "rateLimitResetCredits": null,
              "accountId": null
            }
            """;

        var usage = ProtocolMapper.MapUsage(JsonNode.Parse(json), ReadAt);

        Assert.Equal(20, usage.FiveHour!.UsedPercent);
        Assert.Equal(100, usage.Weekly!.UsedPercent);
        Assert.False(usage.UsageAllowed);
        Assert.Equal("rate_limit_reached", usage.ReachedType);
    }

    [Fact]
    public void Missing_fields_map_to_unknown_not_to_defaults_that_look_safe()
    {
        var json = """{ "rateLimits": { "primary": null, "secondary": null } }""";

        var usage = ProtocolMapper.MapUsage(JsonNode.Parse(json), ReadAt);

        Assert.Null(usage.UsageAllowed);
        Assert.Null(usage.FiveHour);
        Assert.Null(usage.Weekly);
        Assert.False(usage.ResetCreditsReported);
        Assert.Null(usage.Credits);
    }

    [Fact]
    public void Count_only_credit_summary_has_null_details()
    {
        var json = """{ "ordinaryUsageAllowed": true, "rateLimits": {}, "rateLimitResetCredits": { "availableCount": 3, "credits": null } }""";

        var usage = ProtocolMapper.MapUsage(JsonNode.Parse(json), ReadAt);

        Assert.Equal(3, usage.AvailableResetCount);
        Assert.Null(usage.Credits);
    }

    [Theory]
    [InlineData("reset", ResetOutcome.Reset)]
    [InlineData("nothingToReset", ResetOutcome.NothingToReset)]
    [InlineData("noCredit", ResetOutcome.NoCredit)]
    [InlineData("alreadyRedeemed", ResetOutcome.AlreadyRedeemed)]
    public void Maps_every_consume_outcome(string wire, ResetOutcome expected)
    {
        Assert.Equal(expected, ProtocolMapper.MapOutcome(new JsonObject { ["outcome"] = wire }));
    }

    [Fact]
    public void Unknown_consume_outcome_is_an_error_not_a_guess()
    {
        Assert.Throws<CodexProtocolException>(() => ProtocolMapper.MapOutcome(new JsonObject { ["outcome"] = "partial" }));
    }

    [Fact]
    public void Account_mapping_never_carries_the_email()
    {
        var json = """{ "account": { "type": "chatgpt", "email": "someone@example.com", "planType": "plus" }, "requiresOpenaiAuth": true }""";

        var account = ProtocolMapper.MapAccount(JsonNode.Parse(json));

        Assert.True(account.IsAuthenticated);
        Assert.Equal("plus", account.PlanType);
        Assert.DoesNotContain("example.com", account.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Null_account_means_not_logged_in()
    {
        Assert.False(ProtocolMapper.MapAccount(JsonNode.Parse("""{ "account": null, "requiresOpenaiAuth": true }""")).IsAuthenticated);
    }

    [Theory]
    [InlineData("codex-reset-guard/0.159.3 (Windows 10.0.26200; x86_64) term", "0.159.3")]
    [InlineData("codex/1.2.0", "1.2.0")]
    [InlineData("no-version", null)]
    public void Parses_codex_version_from_user_agent(string userAgent, string? expected)
    {
        Assert.Equal(expected, CodexAppServerClient.ParseVersion(userAgent));
    }
}
