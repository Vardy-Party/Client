using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using AutoFixture;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Catalog;
using VardyParty.TestSupport;

namespace VardyParty.Catalog.Tests
{
    public class HomePagePresentationServiceTests
    {
        private readonly IFixture _fixture = AutoMoqFixture.Create();

        private class StubEnriched : IEnrichedGameService, IDisposable
        {
            private readonly Subject<Dictionary<string, List<Game>>?> _subject = new Subject<Dictionary<string, List<Game>>?>();
            private Dictionary<string, List<Game>>? _latest;
            public IObservable<Dictionary<string, List<Game>>?> GamesStream => _subject;
            public IObservable<string?> ErrorStream => new Subject<string?>();

            public Dictionary<string, List<Game>>? GetLatestGames() => _latest;

            public void Push(Dictionary<string, List<Game>>? dict)
            {
                _latest = dict;
                _subject.OnNext(dict);
            }
            public void Dispose() => _subject.Dispose();
        }

        private class PassthroughLeagueFilter : ILeagueFilterService
        {
            public IReadOnlySet<string> DefaultHiddenLeagues => LeagueFilterDefaults.HiddenLeagues;
            public IReadOnlySet<string> HiddenLeagues => new HashSet<string>();
            public event Action? Changed
            {
                add { }
                remove { }
            }

            public bool IsLeagueVisible(string? league) => true;

            public List<Game> FilterGames(IEnumerable<Game>? games) => games?.ToList() ?? new List<Game>();

            public IReadOnlyList<string> GetKnownLeagues(IDictionary<string, List<Game>>? gamesByLeague) =>
                gamesByLeague?.Keys.OrderBy(k => k).ToList() ?? new List<string>();

            public void SetLeagueVisible(string league, bool visible) { }

            public void SetLeaguesVisible(IEnumerable<string> leagues, bool visible) { }

            public void ResetToDefaults() { }
        }

        [Fact]
        public void SubscribesAndPublishesDisplayGroups()
        {
            // Arrange
            var stub = new StubEnriched();
            var svc = new HomePagePresentationService(stub, new PassthroughLeagueFilter());
            List<Game>? received = null;
            var sub = svc.DisplayStream.Subscribe(list => received = list);
            var now = DateTime.UtcNow;
            var g1 = _fixture.Build<Game>()
                .With(g => g.Home, "Home United")
                .With(g => g.Away, "Away City")
                .With(g => g.Start, now.AddMinutes(-10))
                .With(g => g.IsFinished, false)
                .With(g => g.IsInProgress, false)
                .With(g => g.IsHalfTime, false)
                .With(g => g.Minute, (int?)null)
                .With(g => g.StatusText, string.Empty)
                .With(g => g.BBCHome, string.Empty)
                .With(g => g.BBCAway, string.Empty)
                .With(g => g.BBCLeague, string.Empty)
                .Create();
            var dict = new Dictionary<string, List<Game>> { ["League Alpha"] = new List<Game> { g1 } };

            // Act
            stub.Push(dict);

            // Assert
            Assert.NotNull(received);
            Assert.Single(received);

            sub.Dispose();
            svc.Dispose();
            stub.Dispose();
        }

        [Fact]
        public void RepublishesWhenLeagueFilterChanges()
        {
            // Arrange
            var stub = new StubEnriched();
            var filter = new LeagueFilterService(new InMemoryLeagueFilterPreferencesStore());
            var svc = new HomePagePresentationService(stub, filter);
            var received = new List<List<Game>>();
            var sub = svc.DisplayStream.Subscribe(list => received.Add(list));
            var now = DateTime.UtcNow;
            var hiddenLeague = LeagueFilterDefaults.HiddenLeagues.First();
            const string visibleLeague = "League Alpha";
            var visibleGame = _fixture.Build<Game>()
                .With(g => g.League, visibleLeague)
                .With(g => g.Home, "Home United")
                .With(g => g.Away, "Away City")
                .With(g => g.Start, now.AddMinutes(-10))
                .With(g => g.IsFinished, false)
                .With(g => g.IsInProgress, false)
                .With(g => g.IsHalfTime, false)
                .With(g => g.Minute, (int?)null)
                .With(g => g.StatusText, string.Empty)
                .With(g => g.BBCHome, string.Empty)
                .With(g => g.BBCAway, string.Empty)
                .With(g => g.BBCLeague, string.Empty)
                .Create();
            var hiddenGame = _fixture.Build<Game>()
                .With(g => g.League, hiddenLeague)
                .With(g => g.Home, "North FC")
                .With(g => g.Away, "South FC")
                .With(g => g.Start, now.AddMinutes(-10))
                .With(g => g.IsFinished, false)
                .With(g => g.IsInProgress, false)
                .With(g => g.IsHalfTime, false)
                .With(g => g.Minute, (int?)null)
                .With(g => g.StatusText, string.Empty)
                .With(g => g.BBCHome, string.Empty)
                .With(g => g.BBCAway, string.Empty)
                .With(g => g.BBCLeague, string.Empty)
                .Create();
            var dict = new Dictionary<string, List<Game>>
            {
                [visibleLeague] = new List<Game> { visibleGame },
                [hiddenLeague] = new List<Game> { hiddenGame }
            };
            stub.Push(dict);
            var countAfterPush = received.Last().Count;

            // Act
            filter.SetLeagueVisible(hiddenLeague, true);

            // Assert
            Assert.Equal(1, countAfterPush);
            Assert.Equal(2, received.Last().Count);

            sub.Dispose();
            svc.Dispose();
            stub.Dispose();
        }
    }
}
