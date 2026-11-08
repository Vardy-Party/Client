using Moq;
using VardyParty.Ports;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class StreamHealthDataCollectionPreferenceTests
{
    [Fact]
    public void DefaultsOff_AndTogglePersists()
    {
        var store = new Mock<IStreamHealthDataCollectionPreferencesStore>();
        store.Setup(s => s.LoadStreamHealthDataCollectionEnabled()).Returns(false);
        var sut = new StreamHealthDataCollectionPreference(store.Object);

        Assert.False(sut.Enabled);

        sut.SetEnabled(true);
        store.Verify(s => s.SaveStreamHealthDataCollectionEnabled(true), Times.Once);
        Assert.True(sut.Enabled);
    }
}
