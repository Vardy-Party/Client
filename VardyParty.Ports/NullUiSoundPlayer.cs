namespace VardyParty.Ports;

/// <summary>
/// Silent fallback registered with TryAddSingleton for platforms without a
/// native implementation (and for tests).
/// </summary>
public sealed class NullUiSoundPlayer : IUiSoundPlayer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Play(UiSound sound)
    {
    }
}
