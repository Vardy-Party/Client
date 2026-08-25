using VardyParty.Models;

namespace VardyParty.Catalog;

public interface IBbcFixturesService
{
    Task<List<BbcFixture>> GetFixturesAsync(DateOnly fixturePageDate, CancellationToken cancellationToken = default);

    Task<List<BbcFixture>> GetRollingWindowFixturesAsync(CancellationToken cancellationToken = default);
}
