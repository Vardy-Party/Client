using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using VardyParty.Models;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests
{
    public class HomePagePresentationServiceTests
    {
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
            public event Action? Changed;

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
            var stub = new StubEnriched();
            var svc = new HomePagePresentationService(stub, new PassthroughLeagueFilter());

            List<Game>? received = null;
            var sub = svc.DisplayStream.Subscribe(list => received = list);

            var now = DateTime.UtcNow;
            var g1 = new Game { Home = "A", Away = "B", Start = now.AddMinutes(-10), IsFinished = false };
            var dict = new Dictionary<string, List<Game>> { ["L"] = new List<Game> { g1 } };

            stub.Push(dict);

            Assert.NotNull(received);
            Assert.Single(received);

            sub.Dispose();
            svc.Dispose();
            stub.Dispose();
        }

        [Fact]
        public void RepublishesWhenLeagueFilterChanges()
        {
            var stub = new StubEnriched();
            var filter = new LeagueFilterService(new InMemoryLeagueFilterPreferencesStore());
            var svc = new HomePagePresentationService(stub, filter);

            var received = new List<List<Game>>();
            var sub = svc.DisplayStream.Subscribe(list => received.Add(list));

            var now = DateTime.UtcNow;
            var dict = new Dictionary<string, List<Game>>
            {
                ["Premier League"] = new List<Game> { new() { League = "Premier League", Home = "A", Away = "B", Start = now.AddMinutes(-10), IsFinished = false } },
                ["NBA"] = new List<Game> { new() { League = "NBA", Home = "C", Away = "D", Start = now.AddMinutes(-10), IsFinished = false } }
            };

            stub.Push(dict);
            Assert.Single(received.Last());

            filter.SetLeagueVisible("NBA", true);
            Assert.Equal(2, received.Last().Count);

            sub.Dispose();
            svc.Dispose();
            stub.Dispose();
        }
    }
}
