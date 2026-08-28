using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

/// <summary>
/// Every terminal stream-resolution outcome must map to an explicit host plan.
/// The field dead-end these lock in against: no-working-streams (and the
/// gate-refused start) left the picked card latched and said nothing, so
/// re-clicks were swallowed until an app restart.
/// </summary>
public class StreamResolutionOutcomeUxTests
{
    [Fact]
    public void Plan_UserClosed_ClearsSelectionWithoutBanner()
    {
        var plan = StreamResolutionOutcomeUx.Plan(new StreamResolutionOutcome { UserClosed = true });

        Assert.True(plan.ClearSelection);
        Assert.Null(plan.ErrorMessage);
    }

    [Fact]
    public void Plan_NoWorkingStreams_ClearsSelectionAndSaysSo()
    {
        var plan = StreamResolutionOutcomeUx.Plan(new StreamResolutionOutcome { NoWorkingStreams = true });

        Assert.True(plan.ClearSelection);
        Assert.Equal(StreamResolutionOutcomeUx.NoHealthyStreamsMessage, plan.ErrorMessage);
    }

    [Fact]
    public void Plan_StartRefused_ClearsSelectionAndSaysResolverBusy()
    {
        var plan = StreamResolutionOutcomeUx.Plan(new StreamResolutionOutcome { StartRefused = true });

        Assert.True(plan.ClearSelection);
        Assert.Equal(StreamResolutionOutcomeUx.ResolverBusyMessage, plan.ErrorMessage);
    }

    [Fact]
    public void Plan_FailedPlaybackWithMessage_SurfacesThatMessage()
    {
        var outcome = new StreamResolutionOutcome
        {
            PlaybackResult = PlaybackResult.Completed("Decoder gave up")
        };

        var plan = StreamResolutionOutcomeUx.Plan(outcome);

        Assert.True(plan.ClearSelection);
        Assert.Equal("Decoder gave up", plan.ErrorMessage);
    }

    [Fact]
    public void Plan_FailedPlaybackWithoutMessage_FallsBackToStreamUnavailable()
    {
        var outcome = new StreamResolutionOutcome
        {
            PlaybackResult = PlaybackResult.Completed(string.Empty)
        };

        var plan = StreamResolutionOutcomeUx.Plan(outcome);

        Assert.True(plan.ClearSelection);
        Assert.Equal(StreamResolutionOutcomeUx.StreamUnavailableMessage, plan.ErrorMessage);
    }

    [Fact]
    public void Plan_SuccessfulSession_LeavesSelectionToResumeDecision()
    {
        var outcome = new StreamResolutionOutcome
        {
            PlaybackResult = PlaybackResult.SuccessResult("Playback finished")
        };

        var plan = StreamResolutionOutcomeUx.Plan(outcome);

        Assert.False(plan.ClearSelection);
        Assert.Null(plan.ErrorMessage);
    }

    [Theory]
    [InlineData("Boom", "Boom")]
    [InlineData(null, StreamResolutionOutcomeUx.StreamUnavailableMessage)]
    [InlineData(" ", StreamResolutionOutcomeUx.StreamUnavailableMessage)]
    public void PlanException_AlwaysClearsAndAlwaysSaysSomething(string? exceptionMessage, string expected)
    {
        var plan = StreamResolutionOutcomeUx.PlanException(exceptionMessage);

        Assert.True(plan.ClearSelection);
        Assert.Equal(expected, plan.ErrorMessage);
    }
}
