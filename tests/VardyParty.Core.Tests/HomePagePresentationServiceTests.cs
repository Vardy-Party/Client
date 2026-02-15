using System;
using System.Collections.Generic;
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
            public IObservable<Dictionary<string, List<Game>>?> GamesStream => _subject;
            public IObservable<string?> ErrorStream => new Subject<string?>();

            public void Push(Dictionary<string, List<Game>>? dict) => _subject.OnNext(dict);
            public void Dispose() => _subject.Dispose();
        }

        [Fact]
        public void SubscribesAndPublishesDisplayGroups()
        {
            var stub = new StubEnriched();
            var svc = new HomePagePresentationService(stub);

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
    }
}
