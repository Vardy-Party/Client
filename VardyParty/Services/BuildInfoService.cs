using System.Text;

namespace VardyParty.Services;

public record BuildInfo(string Commit, string BuiltAt);

public interface IBuildInfoService
{
    Task<BuildInfo> GetAsync();
}

public class BuildInfoService : IBuildInfoService
{
    private BuildInfo? _cache;
    public async Task<BuildInfo> GetAsync()
    {
        if (_cache != null) return _cache;
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("buildinfo.txt");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync();
            var commit = "unknown";
            var built = "unknown";
            foreach (var part in content.Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                if (kv[0].Equals("Commit", StringComparison.OrdinalIgnoreCase)) commit = kv[1];
                if (kv[0].Equals("Built", StringComparison.OrdinalIgnoreCase)) built = kv[1];
            }
            _cache = new BuildInfo(commit.Trim(), built.Trim());
            return _cache;
        }
        catch
        {
            _cache = new BuildInfo("unknown", "unknown");
            return _cache;
        }
    }
}
