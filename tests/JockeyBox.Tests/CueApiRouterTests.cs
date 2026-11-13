using JockeyBox.Api;
using JockeyBox.Playback;

namespace JockeyBox.Tests;

/// <summary>
/// The HTTP action router, exercised without a listener: every refusal names
/// what was wrong, and every accepted request reaches the deck or the cue bank
/// exactly once. On stream a silent no-op looks like a dead key, so the refusal
/// text and the log line are part of the contract.
/// </summary>
public sealed class CueApiRouterTests
{
    private static CueApiRouter NewRouter(FakeCueTarget target, List<string> log) => new(target, log.Add);

    [Fact]
    public void Unknown_deck_is_refused_with_the_deck_named_and_nothing_reached()
    {
        var target = new FakeCueTarget();
        var log = new List<string>();
        var router = NewRouter(target, log);

        var outcome = router.Handle("POST", "/cue/c/3/recall", null);

        Assert.Equal(400, outcome.Status);
        Assert.Contains("unknown deck 'c'", outcome.Json);
        Assert.Empty(target.Recalls);
        Assert.Contains(log, line => line.Contains("unknown deck 'c'"));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("zz")]
    [InlineData("0")]
    public void Unknown_slot_is_refused(string slot)
    {
        var target = new FakeCueTarget();
        var log = new List<string>();
        var router = NewRouter(target, log);

        var outcome = router.Handle("POST", $"/cue/a/{slot}/recall", null);

        Assert.Equal(400, outcome.Status);
        Assert.Contains($"unknown slot '{slot}'", outcome.Json);
        Assert.Empty(target.Recalls);
        Assert.Single(log);
    }

    [Theory]
    [InlineData("/cue/a/2/delete", 404, "unknown action 'delete'")]
    [InlineData("/cue/a/1", 404, "unknown action")]
    [InlineData("/transport/a/jump", 404, "unknown transport action 'jump'")]
    [InlineData("/transport/c/playpause", 400, "unknown deck 'c'")]
    [InlineData("/transport/sync/master", 404, "unknown sync 'master'")]
    [InlineData("/nope", 404, "unknown action")]
    public void Unknown_actions_are_refused_and_logged(string path, int status, string message)
    {
        var target = new FakeCueTarget();
        var log = new List<string>();
        var router = NewRouter(target, log);

        var outcome = router.Handle("POST", path, null);

        Assert.Equal(status, outcome.Status);
        Assert.Contains(message, outcome.Json);
        Assert.NotEmpty(log);
        Assert.Empty(target.Recalls);
        Assert.Empty(target.PlayPauses);
    }

    [Fact]
    public void Recall_reaches_the_named_deck_and_slot_exactly_once()
    {
        var target = new FakeCueTarget();
        var router = NewRouter(target, new List<string>());

        var outcome = router.Handle("POST", "/cue/b/4/recall", null);

        Assert.Equal(200, outcome.Status);
        var call = Assert.Single(target.Recalls);
        Assert.Equal(PlaybackDeck.B, call.Deck);
        Assert.Equal(4, call.Slot);
        Assert.Contains("\"positionMs\":42000", outcome.Json);
    }

    [Fact]
    public void Quick_cue_aliases_address_slot_zero()
    {
        var target = new FakeCueTarget();
        var router = NewRouter(target, new List<string>());

        Assert.Equal(200, router.Handle("POST", "/cue/a/quick/capture", null).Status);
        Assert.Equal(200, router.Handle("POST", "/cue/a/q/recall", null).Status);

        var captured = Assert.Single(target.Captures);
        Assert.Equal(0, captured.Slot);
        Assert.Equal(0, Assert.Single(target.Recalls).Slot);
    }

    [Fact]
    public void Empty_slot_is_a_409_with_the_reason_and_a_log_line()
    {
        var target = new FakeCueTarget();
        target.Empty.Add((PlaybackDeck.A, 3));
        var log = new List<string>();
        var router = NewRouter(target, log);

        var outcome = router.Handle("POST", "/cue/a/3/recall", null);

        Assert.Equal(409, outcome.Status);
        Assert.Contains("slot 3 is empty", outcome.Json);
        Assert.Contains(log, line => line.Contains("slot 3 is empty"));
        Assert.Empty(target.Recalls);
    }

    [Fact]
    public void Playpause_reaches_the_deck_transport_once()
    {
        var target = new FakeCueTarget();
        var router = NewRouter(target, new List<string>());

        var outcome = router.Handle("POST", "/transport/a/playpause", null);

        Assert.Equal(200, outcome.Status);
        Assert.Equal(PlaybackDeck.A, Assert.Single(target.PlayPauses));
    }

    [Theory]
    [InlineData("", 400)]
    [InlineData("{}", 400)]
    [InlineData("{\"enabled\":\"yes\"}", 400)]
    [InlineData("not json", 400)]
    public void AB_needs_an_explicit_boolean(string body, int status)
    {
        var target = new FakeCueTarget();
        var router = NewRouter(target, new List<string>());

        var outcome = router.Handle("POST", "/transport/ab", body);

        Assert.Equal(status, outcome.Status);
        Assert.Empty(target.ABMode);
    }

    [Fact]
    public void AB_toggle_reaches_the_mode_once()
    {
        var target = new FakeCueTarget();
        var router = NewRouter(target, new List<string>());

        var outcome = router.Handle("POST", "/transport/ab", "{\"enabled\":true}");

        Assert.Equal(200, outcome.Status);
        Assert.True(Assert.Single(target.ABMode));
    }

    [Fact]
    public void Sync_refuses_when_ab_mode_is_off()
    {
        var target = new FakeCueTarget { SyncAvailable = false };
        var log = new List<string>();
        var router = NewRouter(target, log);

        var outcome = router.Handle("POST", "/transport/sync/deck", "{\"enabled\":true}");

        Assert.Equal(409, outcome.Status);
        Assert.Contains("A-B mode is off", outcome.Json);
        Assert.Empty(target.Syncs);
        Assert.NotEmpty(log);
    }

    [Theory]
    [InlineData("deck")]
    [InlineData("volume")]
    [InlineData("tempo")]
    public void Sync_switch_reaches_the_named_switch_once(string name)
    {
        var target = new FakeCueTarget();
        var router = NewRouter(target, new List<string>());

        var outcome = router.Handle("POST", $"/transport/sync/{name}", "{\"enabled\":true}");

        Assert.Equal(200, outcome.Status);
        var call = Assert.Single(target.Syncs);
        Assert.Equal(name, call.Name);
        Assert.True(call.Enabled);
    }

    [Fact]
    public void Snapshot_serves_both_banks_and_the_link_state()
    {
        var target = new FakeCueTarget { SyncAvailable = true };
        var router = NewRouter(target, new List<string>());

        var outcome = router.Handle("GET", "/cues", null);

        Assert.Equal(200, outcome.Status);
        Assert.Contains("\"deck\":\"a\"", outcome.Json);
        Assert.Contains("\"deck\":\"b\"", outcome.Json);
        Assert.Contains("\"hasPosition\":true", outcome.Json);
        Assert.Contains("\"abMode\":true", outcome.Json);
    }

    [Fact]
    public void Get_on_a_post_route_is_refused()
    {
        var target = new FakeCueTarget();
        var router = NewRouter(target, new List<string>());

        var outcome = router.Handle("GET", "/cue/a/1/recall", null);

        Assert.Equal(404, outcome.Status);
        Assert.Empty(target.Recalls);
    }

    [Fact]
    public void Only_cue_and_transport_paths_are_claimed()
    {
        Assert.True(CueApiRouter.Handles("/cues"));
        Assert.True(CueApiRouter.Handles("/cue/a/1/recall"));
        Assert.True(CueApiRouter.Handles("/transport/a/playpause"));
        Assert.False(CueApiRouter.Handles("/sounds"));
        Assert.False(CueApiRouter.Handles("/state"));
    }

    [Fact]
    public void An_internal_failure_is_a_500_that_keeps_the_detail_in_the_log()
    {
        var target = new FakeCueTarget { ThrowOnRecall = true };
        var log = new List<string>();
        var router = NewRouter(target, log);

        var outcome = router.Handle("POST", "/cue/a/1/recall", null);

        Assert.Equal(500, outcome.Status);
        Assert.Contains("internal error", outcome.Json);
        Assert.DoesNotContain("secret", outcome.Json);
        Assert.Contains(log, line => line.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }
}
