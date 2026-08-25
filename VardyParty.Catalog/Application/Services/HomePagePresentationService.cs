using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using VardyParty.Extensions;
using VardyParty.Models;

namespace VardyParty.Catalog;

public interface IHomePagePresentationService
{
    IObservable<List<Game>> DisplayStream { get; }
}

public class HomePagePresentationService : IHomePagePresentationService, IDisposable
{
    private readonly Subject<List<Game>> _subject = new Subject<List<Game>>();
    private readonly IDisposable _subscription;
    private readonly ILeagueFilterService _leagueFilter;
    private Dictionary<string, List<Game>>? _latestSnapshot;

    public IObservable<List<Game>> DisplayStream => _subject;

    public HomePagePresentationService(IEnrichedGameService enriched, ILeagueFilterService leagueFilter)
    {
        if (enriched == null) throw new ArgumentNullException(nameof(enriched));
        _leagueFilter = leagueFilter ?? throw new ArgumentNullException(nameof(leagueFilter));

        _leagueFilter.Changed += OnLeagueFilterChanged;

        _subscription = enriched.GamesStream.Subscribe(dict =>
        {
            _latestSnapshot = dict;
            PublishDisplay();
        });
    }

    private void OnLeagueFilterChanged()
    {
        PublishDisplay();
    }

    private void PublishDisplay()
    {
        try
        {
            var display = _latestSnapshot?.ToDisplay() ?? new List<Game>();
            var filtered = _leagueFilter.FilterGames(display);
            _subject.OnNext(filtered);
        }
        catch (Exception)
        {
            // swallow - presentation should be robust; optionally log if logger available
        }
    }

    public void Dispose()
    {
        _leagueFilter.Changed -= OnLeagueFilterChanged;
        _subscription.Dispose();
        _subject.Dispose();
    }
}
