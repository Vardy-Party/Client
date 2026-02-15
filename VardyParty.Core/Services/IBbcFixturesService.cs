using VardyParty.Models;

namespace VardyParty.Services;

public interface IBbcFixturesService
{
    Task<List<BbcFixture>> GetFixturesAsync(DateTime dateUtc);
}
