using System;
using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using VardyParty.Kernel;
using VardyParty.Presentation;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Presentation.Tests;

/// <summary>
/// Sticky-ordering rules for in-place homepage refreshes: rows keep their
/// positions across polls, re-tiering only on live-set transitions, the
/// focused row never moves, and card order inside a row is stable.
/// </summary>
public class HomeBoardDifferTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    private static readonly string[] NoLive = Array.Empty<string>();

    // ------------------------------------------------------------- game key --

    [Fact]
    public void GameKey_SameFixtureDifferentCasing_MatchesSameGameSemantics()
    {
        // Arrange
        var a = Fixture("Home United", "Away City");
        var b = Fixture("HOME UNITED", "away city");

        // Act
        var keyA = HomeBoardDiffer.GameKey(a);
        var keyB = HomeBoardDiffer.GameKey(b);

        // Assert
        Assert.Equal(keyA, keyB);
        Assert.True(HomePlaybackIntent.SameGame(a, b));
    }

    [Fact]
    public void GameKey_DifferentFixtures_Differ()
    {
        // Arrange
        var a = Fixture("Home United", "Away City");
        var b = Fixture("Home United", "North Rovers");

        // Act
        var keyA = HomeBoardDiffer.GameKey(a);
        var keyB = HomeBoardDiffer.GameKey(b);

        // Assert
        Assert.NotEqual(keyA, keyB);
    }

    // ----------------------------------------------------------- row orders --

    [Fact]
    public void PlanRowOrder_SameBoardAgain_KeepsOrderExactly()
    {
        // Arrange
        var target = Board(("League Alpha", false), ("Cup Beta", false), ("League Gamma", false));
        var current = new[] { "League Alpha", "Cup Beta", "League Gamma" };

        // Act
        var planned = HomeBoardDiffer.PlanRowOrder(current, target, NoLive, focusedLeague: null);

        // Assert
        Assert.Equal(current, planned.Select(r => r.League));
    }

    [Fact]
    public void PlanRowOrder_TargetReordersButLiveSetUnchanged_StaysSticky()
    {
        // Arrange: the builder now wants Gamma first (earlier kickoff), but no
        // live-set transition happened — the shown order must not move.
        var target = Board(("League Gamma", false), ("League Alpha", false), ("Cup Beta", false));
        var current = new[] { "League Alpha", "Cup Beta", "League Gamma" };

        // Act
        var planned = HomeBoardDiffer.PlanRowOrder(current, target, NoLive, focusedLeague: null);

        // Assert
        Assert.Equal(current, planned.Select(r => r.League));
    }

    [Fact]
    public void PlanRowOrder_ReTierOnlyWhenLiveSetChanges_UsesTargetOrder()
    {
        // Arrange: Cup Beta gained its first live game — a delivered live-set
        // transition — so the builder's live-first order applies.
        var target = Board(("Cup Beta", true), ("League Alpha", false), ("League Gamma", false));
        var current = new[] { "League Alpha", "Cup Beta", "League Gamma" };

        // Act
        var planned = HomeBoardDiffer.PlanRowOrder(current, target, NoLive, focusedLeague: null);

        // Assert
        Assert.Equal(new[] { "Cup Beta", "League Alpha", "League Gamma" }, planned.Select(r => r.League));
    }

    [Fact]
    public void PlanRowOrder_SameLiveSetAcrossPolls_DoesNotReshuffle()
    {
        // Arrange: Cup Beta is still live on the next poll (set unchanged) and
        // the builder still wants it first — but no transition, so no move.
        var target = Board(("Cup Beta", true), ("League Alpha", false), ("League Gamma", false));
        var current = new[] { "League Alpha", "Cup Beta", "League Gamma" };
        var previousLive = new[] { "Cup Beta" };

        // Act
        var planned = HomeBoardDiffer.PlanRowOrder(current, target, previousLive, focusedLeague: null);

        // Assert
        Assert.Equal(current, planned.Select(r => r.League));
    }

    [Fact]
    public void PlanRowOrder_ReTier_NeverMovesTheFocusedRow()
    {
        // Arrange: live-set transition wants Gamma on top, but the user's
        // focus sits in League Alpha (index 0) — Alpha must stay at 0.
        var target = Board(("League Gamma", true), ("League Alpha", false), ("Cup Beta", false));
        var current = new[] { "League Alpha", "Cup Beta", "League Gamma" };

        // Act
        var planned = HomeBoardDiffer.PlanRowOrder(current, target, NoLive, focusedLeague: "League Alpha");

        // Assert
        Assert.Equal("League Alpha", planned[0].League);
        Assert.Equal(new[] { "League Alpha", "League Gamma", "Cup Beta" }, planned.Select(r => r.League));
    }

    [Fact]
    public void PlanRowOrder_NewLeague_InsertsAtTargetRelativePosition()
    {
        // Arrange: Cup Beta is new and the builder places it between the two
        // existing leagues.
        var target = Board(("League Alpha", false), ("Cup Beta", false), ("League Gamma", false));
        var current = new[] { "League Alpha", "League Gamma" };

        // Act
        var planned = HomeBoardDiffer.PlanRowOrder(current, target, NoLive, focusedLeague: null);

        // Assert
        Assert.Equal(new[] { "League Alpha", "Cup Beta", "League Gamma" }, planned.Select(r => r.League));
    }

    [Fact]
    public void PlanRowOrder_NewLeague_NeverDisplacesTheFocusedRow()
    {
        // Arrange: the builder wants the new league ABOVE the focused row —
        // inserting there would push the focused row down, so it lands
        // directly below instead.
        var target = Board(("Cup Beta", false), ("League Alpha", false), ("League Gamma", false));
        var current = new[] { "League Alpha", "League Gamma" };

        // Act
        var planned = HomeBoardDiffer.PlanRowOrder(current, target, NoLive, focusedLeague: "League Alpha");

        // Assert
        Assert.Equal("League Alpha", planned[0].League);
        Assert.Equal(new[] { "League Alpha", "Cup Beta", "League Gamma" }, planned.Select(r => r.League));
    }

    [Fact]
    public void PlanRowOrder_RemovedLeague_IsDropped()
    {
        // Arrange
        var target = Board(("League Alpha", false), ("League Gamma", false));
        var current = new[] { "League Alpha", "Cup Beta", "League Gamma" };

        // Act
        var planned = HomeBoardDiffer.PlanRowOrder(current, target, NoLive, focusedLeague: null);

        // Assert
        Assert.Equal(new[] { "League Alpha", "League Gamma" }, planned.Select(r => r.League));
    }

    [Fact]
    public void PlanRowOrder_LeagueKeysAreCaseInsensitive()
    {
        // Arrange: BBC enrichment can change casing; that is the same row.
        var target = Board(("LEAGUE ALPHA", false), ("Cup Beta", false));
        var current = new[] { "League Alpha", "Cup Beta" };

        // Act
        var planned = HomeBoardDiffer.PlanRowOrder(current, target, NoLive, focusedLeague: null);

        // Assert
        Assert.Equal(2, planned.Count);
        Assert.Equal("LEAGUE ALPHA", planned[0].League);
    }

    [Fact]
    public void PlanRowOrder_EmptyCurrent_UsesTargetOrder()
    {
        // Arrange: the initial board.
        var target = Board(("Cup Beta", true), ("League Alpha", false));

        // Act
        var planned = HomeBoardDiffer.PlanRowOrder(
            Array.Empty<string>(), target, NoLive, focusedLeague: null);

        // Assert
        Assert.Equal(new[] { "Cup Beta", "League Alpha" }, planned.Select(r => r.League));
    }

    // ---------------------------------------------------------- card orders --

    [Fact]
    public void PlanCardOrder_TargetReorders_ExistingCardsStayPut()
    {
        // Arrange: minute changes re-sort the builder's in-row order, but the
        // shown card order must stay stable across refreshes.
        var first = Fixture("Home United", "Away City");
        var second = Fixture("North Rovers", "South Wanderers");
        var current = new[] { HomeBoardDiffer.GameKey(first), HomeBoardDiffer.GameKey(second) };

        // Act: target order flipped.
        var planned = HomeBoardDiffer.PlanCardOrder(current, new[] { second, first });

        // Assert
        Assert.Equal(
            new[] { HomeBoardDiffer.GameKey(first), HomeBoardDiffer.GameKey(second) },
            planned.Select(HomeBoardDiffer.GameKey));
    }

    [Fact]
    public void PlanCardOrder_NewCard_InsertsAtTargetRelativePosition()
    {
        // Arrange
        var first = Fixture("Home United", "Away City");
        var added = Fixture("East Athletic", "West Albion");
        var last = Fixture("North Rovers", "South Wanderers");
        var current = new[] { HomeBoardDiffer.GameKey(first), HomeBoardDiffer.GameKey(last) };

        // Act: the new fixture sits between the two existing ones.
        var planned = HomeBoardDiffer.PlanCardOrder(current, new[] { first, added, last });

        // Assert
        Assert.Equal(
            new[] { first, added, last }.Select(HomeBoardDiffer.GameKey),
            planned.Select(HomeBoardDiffer.GameKey));
    }

    [Fact]
    public void PlanCardOrder_RemovedCard_IsDropped()
    {
        // Arrange
        var kept = Fixture("Home United", "Away City");
        var gone = Fixture("North Rovers", "South Wanderers");
        var current = new[] { HomeBoardDiffer.GameKey(gone), HomeBoardDiffer.GameKey(kept) };

        // Act
        var planned = HomeBoardDiffer.PlanCardOrder(current, new[] { kept });

        // Assert
        var only = Assert.Single(planned);
        Assert.Equal(HomeBoardDiffer.GameKey(kept), HomeBoardDiffer.GameKey(only));
    }

    [Fact]
    public void PlanCardOrder_ReturnsTargetInstances_ForUpdatedData()
    {
        // Arrange: the same fixture arrives as a fresh Game instance with a
        // new score — the plan must hand back the fresh instance.
        var stale = Fixture("Home United", "Away City");
        var fresh = Fixture("Home United", "Away City");
        var current = new[] { HomeBoardDiffer.GameKey(stale) };

        // Act
        var planned = HomeBoardDiffer.PlanCardOrder(current, new[] { fresh });

        // Assert
        Assert.Same(fresh, Assert.Single(planned));
    }

    // ------------------------------------------------------------- fixtures --

    private Game Fixture(string home, string away) =>
        _fixture.Build<Game>()
            .With(g => g.Home, home)
            .With(g => g.Away, away)
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.Start, DateTime.UtcNow)
            .Create();

    private LeagueRowModel RowModel(string league, bool live)
    {
        var game = _fixture.Build<Game>()
            .With(g => g.Home, $"{league} Home")
            .With(g => g.Away, $"{league} Away")
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.League, league)
            .With(g => g.BBCLeague, "")
            .With(g => g.IsInProgress, live)
            .With(g => g.IsHalfTime, false)
            .With(g => g.IsFinished, false)
            .With(g => g.Minute, live ? 30 : (int?)null)
            .With(g => g.StatusText, "")
            .With(g => g.Start, DateTime.UtcNow)
            .Create();

        return new LeagueRowModel(league, new List<Game> { game });
    }

    private IReadOnlyList<LeagueRowModel> Board(params (string League, bool Live)[] rows) =>
        rows.Select(r => RowModel(r.League, r.Live)).ToList();
}
