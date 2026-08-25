using VardyParty.Kernel;

namespace VardyParty.Catalog;

public interface IBbcHtmlParser
{
    List<BbcFixture> ParseHtml(string html, CancellationToken cancellationToken = default);
}
