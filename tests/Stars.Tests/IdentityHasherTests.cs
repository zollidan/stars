using System.Text.Json;
using Stars.Application.Ingestion;
using Stars.Connectors.Abstractions;

namespace Stars.Tests;

public class IdentityHasherTests
{
    [Fact]
    public void Same_observation_has_same_hash()
    {
        var first = new SourceEntityRecord("team", "team:1", "1", "Spartak Moscow", "football", "RU");
        var second = new SourceEntityRecord("team", "team:1", "1", "Spartak Moscow", "football", "RU");

        Assert.Equal(IdentityHasher.Entity(first), IdentityHasher.Entity(second));
    }

    [Fact]
    public void Volatile_attributes_do_not_change_identity()
    {
        var plain = new SourceEntityRecord("team", "team:1", "1", "Spartak Moscow", "football", "RU");
        var rich = plain with
        {
            Attributes = JsonSerializer.SerializeToElement(new { odds_snapshot = 123, colour = "red" }),
        };

        Assert.Equal(IdentityHasher.Entity(plain), IdentityHasher.Entity(rich));
    }

    [Fact]
    public void Rename_changes_identity_hash()
    {
        var oldName = new SourceEntityRecord("team", "team:1", "1", "Spartak Moscow", "football", "RU");
        var newName = oldName with { DisplayName = "Spartak St Petersburg" };

        Assert.NotEqual(IdentityHasher.Entity(oldName), IdentityHasher.Entity(newName));
    }

    [Fact]
    public void Event_hash_is_order_independent_for_participants()
    {
        var home = new SourceEventParticipantRecord("team:1", "home");
        var away = new SourceEventParticipantRecord("team:2", "away");
        var first = new SourceEventRecord(
            "match:1", "1", "football", "league", DateTimeOffset.UnixEpoch, "scheduled", [home, away]);
        var second = first with { Participants = [away, home] };

        Assert.Equal(
            IdentityHasher.Event(first, "league"),
            IdentityHasher.Event(second, "league"));
    }
}
