using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using VardyParty.Extensions;
using VardyParty.Models;

namespace VardyParty.Services;

    public interface IHomePagePresentationService
    {
        IObservable<List<Game>> DisplayStream { get; }
    }

    public class HomePagePresentationService : IHomePagePresentationService, IDisposable
    {
        private readonly Subject<List<Game>> _subject = new Subject<List<Game>>();
        public IObservable<List<Game>> DisplayStream => _subject;

        private readonly IDisposable _subscription;

        public HomePagePresentationService(IEnrichedGameService enriched)
        {
            if (enriched == null) throw new ArgumentNullException(nameof(enriched));

            _subscription = enriched.GamesStream.Subscribe(dict =>
            {
                try
                {
                    var display = dict?.ToDisplay() ?? new List<Game>();
                    _subject.OnNext(display);
                }
                catch (Exception)
                {
                    // swallow - presentation should be robust; optionally log if logger available
                }
            });
        }

        public void Dispose()
        {
            _subscription.Dispose();
            _subject.Dispose();
        }
    }
