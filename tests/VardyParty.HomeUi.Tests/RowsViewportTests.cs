using VardyParty.HomeUi.Views;
using Xunit;

namespace VardyParty.HomeUi.Tests;

public class RowsViewportTests
{
    [Fact]
    public void HeightRequest_UnmeasuredHost_IsNull()
    {
        Assert.Null(RowsViewport.HeightRequest(hostHeight: 0, currentRequest: -1));
        Assert.Null(RowsViewport.HeightRequest(hostHeight: -4, currentRequest: 200));
    }

    [Fact]
    public void HeightRequest_PinsTheInnerViewportToTheArrangedCell()
    {
        // The CollectionView's default request is -1 (stretch). The inner
        // scroll viewport then sizes to a content estimate and the leftover
        // cell paints black — pin the request to the host height.
        var next = RowsViewport.HeightRequest(hostHeight: 720, currentRequest: -1);

        Assert.Equal(720, next);
    }

    [Fact]
    public void HeightRequest_AlreadyPinned_IsNullToAvoidALayoutLoop()
    {
        Assert.Null(RowsViewport.HeightRequest(hostHeight: 720, currentRequest: 720));
        Assert.Null(RowsViewport.HeightRequest(hostHeight: 720, currentRequest: 720.2));
    }

    [Fact]
    public void HeightRequest_HostGrew_UpdatesThePin()
    {
        var next = RowsViewport.HeightRequest(hostHeight: 900, currentRequest: 720);

        Assert.Equal(900, next);
    }
}
