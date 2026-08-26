using VardyParty.Catalog;
using VardyParty.HomeUi;
using VardyParty.Kernel;

namespace VardyParty.Desktop.Services;

/// <summary>
/// Connects the shared homepage to the catalog: subscribes the enriched games
/// stream and error stream into <see cref="HomeViewModel"/> and starts the
/// background pollers. Set VARDYPARTY_DESKTOP_SAMPLE_DATA=1 to render a
/// fabricated catalog instead (useful for demos and headless smoke tests
/// while auth is stubbed).
/// </summary>
public sealed class HomeFeed : IDisposable
{
    private readonly IEnrichedGameService _games;
    private readonly HomeViewModel _home;
    private readonly List<IDisposable> _subscriptions = new();
    private bool _started;

    public HomeFeed(IEnrichedGameService games, HomeViewModel home)
    {
        _games = games;
        _home = home;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        if (Environment.GetEnvironmentVariable("VARDYPARTY_DESKTOP_SAMPLE_DATA") == "1")
        {
            _home.UpdateGames(SampleGames.Build());
            return;
        }

        _subscriptions.Add(_games.GamesStream.Subscribe(new DelegateObserver<Dictionary<string, List<Game>>?>(
            dict => _home.UpdateGames(dict))));
        _subscriptions.Add(_games.ErrorStream.Subscribe(new DelegateObserver<string?>(
            error => _home.SetError(error))));

        (_games as EnrichedGameService)?.StartBackgroundPolling();
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
    }

    private sealed class DelegateObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;

        public DelegateObserver(Action<T> onNext) => _onNext = onNext;

        public void OnNext(T value) => _onNext(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
