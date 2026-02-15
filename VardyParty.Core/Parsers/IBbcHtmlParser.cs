using VardyParty.Models;

namespace VardyParty.Parsers;

public interface IBbcHtmlParser
{
    List<BbcFixture> ParseHtml(string html, CancellationToken cancellationToken = default);
}
