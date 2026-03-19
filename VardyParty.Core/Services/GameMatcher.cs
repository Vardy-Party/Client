using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VardyParty.Models;

namespace VardyParty.Services;

public class GameMatcher(ILogger<GameMatcher> logger) : IGameMatcher
{
    private static readonly HashSet<string> TeamStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fc", "cf", "sc", "club", "afc" 
        // Removed "al" to fix "Al Hazm" vs "Al Hazem" where stripping "Al" leaves very short strings "Hazm"/"Hazem"
        // making Levenshtein distance proportionally too expensive.
    };

    // Team name aliases for known variations that fuzzy matching won't catch
    // Applied AFTER diacritic removal and FC/CF/SC prefix stripping
    private static readonly Dictionary<string, string> TeamAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // UEFA Champions League variations (after normalization)
        { "Kobenhavn", "Copenhagen" },  // Handles: København, FC København, FC Kobenhavn
        { "Internazionale", "Inter Milan" },
        
        // Add more as needed
    };

    public void EnrichGames(List<Game> games, List<BbcFixture> bbcFixtures, string leagueLabel)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int gameCount = games?.Count ?? 0;
        int bbcCount = bbcFixtures?.Count ?? 0;
        logger.LogInformation("[Matcher] EnrichGames start. Games={GameCount} BBC={BbcCount} Label={Label}", gameCount, bbcCount, leagueLabel);

        if (games == null || games.Count == 0 || bbcFixtures == null || bbcFixtures.Count == 0) 
        {
            logger.LogInformation("[Matcher] EnrichGames skipped (empty input)");
            return;
        }

        var map = bbcFixtures.ToDictionary(f => Key(f.Home, f.Away), f => f, StringComparer.OrdinalIgnoreCase);
        // Use ConcurrentDictionary for thread-safe tracking of matched keys
        var matchedKeys = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        var preProcSw = System.Diagnostics.Stopwatch.StartNew();
        var bbcCandidates = bbcFixtures.Select(f => new
        {
            Fixture = f,
            HomeComparable = BuildComparableName(f.Home),
            AwayComparable = BuildComparableName(f.Away),
            HomeRaw = NormalizeTeamName(f.Home),
            AwayRaw = NormalizeTeamName(f.Away),
            KickoffUtc = f.KickoffUtc
        }).ToArray();
        preProcSw.Stop();
        logger.LogInformation("[Matcher] Pre-processing BBC candidates took {Elapsed}ms", preProcSw.ElapsedMilliseconds);

        var loopSw = System.Diagnostics.Stopwatch.StartNew();
        // Parallelize matching logic (CPU bound)
        Parallel.ForEach(games, g =>
        {
            if (g.IsOlympicLeague)
            {
                return;
            }

            var gameKey = Key(g.Home, g.Away);
            if (map.TryGetValue(gameKey, out var bbc))
            {
                matchedKeys.TryAdd(gameKey, 0);
                EnrichGame(g, bbc);
                return; // Continue to next game
            }

            var gHomeComparable = BuildComparableName(g.Home);
            var gAwayComparable = BuildComparableName(g.Away);
            var startUtc = NormalizeUtc(g.Start, DateTime.UtcNow);

            // Single pass to find best match across all criteria tiers
            // Tier 1: High Similarity (Home>=0.55, Away>=0.55, Pair>=0.60)
            // Tier 2: Tolerance (Home>=0.50, Away>=0.50, Pair>=0.50, TimeDiff<=30)
            // Tier 3: Asymmetric (One>=0.80, Other>=0.45, TimeDiff<=30)

            // Track best candidates for each tier
            double t1BestScore = double.MinValue; BbcFixture? t1Fixture = null;
            double t2BestScore = double.MinValue; BbcFixture? t2Fixture = null;
            double t3BestScore = double.MinValue; BbcFixture? t3Fixture = null;

            // Pre-calculate expensive comparisons if possible or fail fast
            for (int i = 0; i < bbcCandidates.Length; i++)
            {
                var b = bbcCandidates[i];
                var homeScore = ComputeNameSimilarity(gHomeComparable, b.HomeComparable, g.Home, b.HomeRaw);
                var awayScore = ComputeNameSimilarity(gAwayComparable, b.AwayComparable, g.Away, b.AwayRaw);
                var pairScore = (homeScore + awayScore) / 2.0;
                
                // Tier 1
                if (homeScore >= 0.55 && awayScore >= 0.55 && pairScore >= 0.60)
                {
                    if (pairScore > t1BestScore)
                    {
                        t1BestScore = pairScore;
                        t1Fixture = b.Fixture;
                    }
                }

                var timeDiff = b.KickoffUtc == DateTime.MinValue ? double.MaxValue : Math.Abs((b.KickoffUtc - startUtc).TotalMinutes);
                bool timeOk = timeDiff == double.MaxValue || timeDiff <= 30;

                if (timeOk)
                {
                    // Tier 2
                    if (homeScore >= 0.50 && awayScore >= 0.50 && pairScore >= 0.50)
                    {
                        if (pairScore > t2BestScore)
                        {
                            t2BestScore = pairScore;
                            t2Fixture = b.Fixture;
                        }
                    }

                    // Tier 3
                    if ((homeScore >= 0.80 && awayScore >= 0.45) || (awayScore >= 0.80 && homeScore >= 0.45))
                    {
                        if (pairScore > t3BestScore)
                        {
                            t3BestScore = pairScore;
                            t3Fixture = b.Fixture;
                        }
                    }
                }
            }

            // Prioritize matches: Tier 1 > Tier 2 > Tier 3
            var bestFixture = t1Fixture ?? t2Fixture ?? t3Fixture;

            if (bestFixture != null)
            {
                matchedKeys.TryAdd(gameKey, 0);
                EnrichGame(g, bestFixture);
            }
        });
        loopSw.Stop();
        logger.LogInformation("[Matcher] Parallel matching loop took {Elapsed}ms for {Count} games", loopSw.ElapsedMilliseconds, gameCount);

        foreach (var g in games)
        {
            if (!g.IsFinished && g.Minute.HasValue)
            {
                g.IsInProgress = true;
            }
        }

        var now = DateTime.UtcNow;
        foreach (var g in games)
        {
            var startUtc = NormalizeUtc(g.Start, now);

            if (startUtc > now.AddMinutes(5) && !g.IsInProgress && !g.IsHalfTime && !g.IsFinished)
            {
                g.IsFinished = false;
                g.IsInProgress = false;
                g.IsHalfTime = false;
                g.Minute = null;
                g.StatusText = string.Empty;
            }
            else if (startUtc > now.AddHours(-6))
            {
                g.IsHalfTime = g.IsHalfTime && startUtc <= now;
                if (!g.IsInProgress && startUtc > now)
                {
                    g.StatusText = string.Empty;
                }
            }

            if (!matchedKeys.ContainsKey(Key(g.Home, g.Away))
                && startUtc < now.AddHours(g.IsOlympicLeague ? -5 : -2))
            {
                g.IsFinished = true;
            }
        }
        
        sw.Stop();
        logger.LogInformation("[Matcher] EnrichGames total duration: {Elapsed}ms", sw.ElapsedMilliseconds);
    }

    private static void EnrichGame(Game g, BbcFixture bbc)
    {
        if (bbc.KickoffUtc != DateTime.MinValue)
        {
            g.Start = bbc.KickoffUtc;
        }

        g.AggregateHomeScore = bbc.AggregateHomeScore;
        g.AggregateAwayScore = bbc.AggregateAwayScore;

        if (!bbc.HasProgress)
        {
            g.IsFinished = false;
            g.IsInProgress = false;
            g.IsHalfTime = false;
            g.Minute = null;
            
            // Postponed games have HasProgress=false but carry significant Status
            if (!string.IsNullOrEmpty(bbc.Status) && bbc.Status.Contains("Postponed", StringComparison.OrdinalIgnoreCase))
            {
                g.StatusText = bbc.Status;
            }
            else
            {
                g.StatusText = string.Empty;
            }

            g.HomeScore = bbc.HomeScore;
            g.AwayScore = bbc.AwayScore;
        }
        else
        {
            g.HomeScore = bbc.HomeScore;
            g.AwayScore = bbc.AwayScore;
            g.IsFinished = bbc.IsFinished;
            g.IsInProgress = bbc.IsInProgress;
            g.IsHalfTime = bbc.IsHalfTime;
            g.Minute = bbc.Minute;
            g.StatusText = BuildStatusText(bbc);
        }

        if (g.IsHalfTime && !g.IsFinished)
        {
            g.IsInProgress = true;
        }

        if (!string.IsNullOrEmpty(bbc.HomeBadgeUrl)) g.HomeBadgeUrl = bbc.HomeBadgeUrl;
        if (!string.IsNullOrEmpty(bbc.AwayBadgeUrl)) g.AwayBadgeUrl = bbc.AwayBadgeUrl;

        g.BBCHome = bbc.Home ?? string.Empty;
        g.BBCAway = bbc.Away ?? string.Empty;
        g.BBCLeague = bbc.League ?? string.Empty;

        if (!string.IsNullOrEmpty(bbc.League))
        {
            g.League = bbc.League;
        }
    }

    private static string BuildStatusText(BbcFixture f)
    {
        if (f.IsFinished) return "FT";
        if (f.IsHalfTime) return "HT";
        if (f.Minute.HasValue)
        {
            if (f.Minute.Value >= 1000)
            {
                var baseMin = f.Minute.Value / 100;
                var extra = f.Minute.Value % 100;
                return $"{baseMin}+{extra}'";
            }
            return $"{f.Minute}'";
        }
        if (f.IsInProgress) return string.IsNullOrEmpty(f.Status) ? "Live" : f.Status;
        return string.IsNullOrEmpty(f.Status) ? string.Empty : f.Status;
    }

    private static string Key(string home, string away) => $"{NormalizeTeamName(home)}|{NormalizeTeamName(away)}";

    private static string NormalizeTeamName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        
        var decoded = WebUtility.HtmlDecode(name) ?? string.Empty;

        // Step 1: Semantic replacements for common diacritics to ensure ASCII match
        var replaced = decoded
            .Replace("İ", "i").Replace("ı", "i")
            .Replace("Ş", "s").Replace("ş", "s")
            .Replace("Ğ", "g"). Replace("ğ", "g")
            .Replace("Ü", "u"). Replace("ü", "u")
            .Replace("Ö", "o"). Replace("ö", "o")
            .Replace("Ç", "c"). Replace("ç", "c")
            .Replace("Á", "a"). Replace("á", "a")
            .Replace("É", "e"). Replace("é", "e")
            .Replace("Í", "i"). Replace("í", "i")
            .Replace("Ó", "o"). Replace("ó", "o")
            .Replace("Ú", "u"). Replace("ú", "u")
            .Replace("Ñ", "n"). Replace("ñ", "n")
            .Replace("ø", "o"). Replace("Ø", "o")  // Danish ø
            .Replace("å", "a"). Replace("Å", "a")  // Danish å
            .Replace("æ", "ae"). Replace("Æ", "ae"); // Danish æ

        var lower = replaced.Trim().ToLowerInvariant();

        // Step 2: Remove Unicode combining marks
        lower = lower.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var c in lower)
        {
            var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        lower = sb.ToString().Normalize(System.Text.NormalizationForm.FormC);

        lower = lower.Replace("-", " ").Replace(".", " ");
        lower = lower.Replace("&", "and");
        lower = RegexReplace(lower, "\\butd\\b", "united");
        lower = RegexReplace(lower, "\\s+", " ").Trim();

        // Step 3: Remove common prefixes before alias check
        if (lower.StartsWith("afc "))
        {
            lower = lower.Substring(4).Trim();
        }
        if (lower.StartsWith("fc "))
        {
            lower = lower.Substring(3).Trim();
        }
        if (lower.StartsWith("cf "))
        {
            lower = lower.Substring(3).Trim();
        }
        if (lower.StartsWith("sc "))
        {
            lower = lower.Substring(3).Trim();
        }

        // Step 4: Check for known aliases AFTER normalization
        // This allows "København" → "kobenhavn" → alias match
        if (TeamAliases.TryGetValue(lower, out var alias))
        {
            return alias.ToLowerInvariant();
        }

        return lower;
    }

    private static string BuildComparableName(string? name)
    {
        var normalized = NormalizeTeamName(name);
        if (string.IsNullOrEmpty(normalized)) return string.Empty;
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !TeamStopwords.Contains(t))
            .ToArray();
        return string.Join(' ', tokens);
    }

    private static double ComputeNameSimilarity(string aComparable, string bComparable, string? aRaw, string? bRaw)
    {
        // Optimization: Fail fast if one string is significantly longer than the other (length diff > 70%) unless very short
        // This avoids Levenshtein on clearly mismatched names
        if (string.IsNullOrEmpty(aComparable) || string.IsNullOrEmpty(bComparable)) return 0;
        
        // Quick containment check - very cheap and high signal
        if (bComparable.IndexOf(aComparable, StringComparison.OrdinalIgnoreCase) >= 0) return 0.95;
        if (aComparable.IndexOf(bComparable, StringComparison.OrdinalIgnoreCase) >= 0) return 0.95; // Symmetric check needed

        var aTokens = aComparable.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bTokens = bComparable.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Check Acronyms (e.g. PSG matches Paris Saint-Germain)
        // If one side is a single token and short, try to match it as acronym of the other
        if (aTokens.Length == 1 && aTokens[0].Length <= 5 && bTokens.Length > 1)
        {
            if (IsAcronymMatch(aTokens[0], bTokens)) return 0.95;
        }
        if (bTokens.Length == 1 && bTokens[0].Length <= 5 && aTokens.Length > 1)
        {
            if (IsAcronymMatch(bTokens[0], aTokens)) return 0.95;
        }

        // Heuristic: allow prefix subset on first token when ending tokens match (e.g., "man united" vs "manchester united")
        if (aTokens.Length >= 2 && bTokens.Length >= 2)
        {
            var aLast = aTokens[^1];
            var bLast = bTokens[^1];
            if (string.Equals(aLast, bLast, StringComparison.OrdinalIgnoreCase))
            {
                var aFirst = aTokens[0];
                var bFirst = bTokens[0];
                if (bFirst.StartsWith(aFirst, StringComparison.OrdinalIgnoreCase) || aFirst.StartsWith(bFirst, StringComparison.OrdinalIgnoreCase))
                {
                    return 0.90;
                }
            }
        }

        // Token intersection check (Jaccard-ish) - cheaper than Levenshtein
        // Optimization: Use Any() first to fail fast on completely disjoint sets if possible, but Count() is needed for score.
        int intersectCount = 0;
        foreach (var ta in aTokens)
        {
            foreach (var tb in bTokens)
            {
                if (string.Equals(ta, tb, StringComparison.OrdinalIgnoreCase))
                {
                    intersectCount++;
                    break; // match each token only once roughly
                }
            }
        }
        
        int union = (aTokens.Length + bTokens.Length) - intersectCount;
        var tokenScore = union == 0 ? 0 : (double)intersectCount / union;

        // If token score is extremely low, Levenshtein is unlikely to rescue it unless it's a typo.
        // Skip Levenshtein if tokens are totally disparate and length difference is huge.
        if (tokenScore < 0.1 && Math.Abs(aComparable.Length - bComparable.Length) > 5) 
        {
             return tokenScore * 0.55; 
        }

        var aNormRaw = NormalizeTeamName(aRaw);
        var bNormRaw = NormalizeTeamName(bRaw);
        
        // Skip expensive Levenshtein if length allows
        var maxLen = Math.Max(aNormRaw.Length, bNormRaw.Length);
        if (maxLen == 0) return 0;

        // Optimization: Thresholded Levenshtein? No, we need actual score.
        // But we can skip if token score is high enough? No, combined score is needed.
        
        var dist = ComputeLevenshteinDistance(aNormRaw, bNormRaw);
        var levScore = 1.0 - (double)dist / maxLen;

        // Allow high Levenshtein score to override token mismatch for single-word names (e.g. typos like "Cagilari" vs "Cagliari")
        // But enforce length check to avoid matching short distinct words (e.g. "Roma" vs "Como")
        if (tokenScore < 0.2 && aTokens.Length == 1 && bTokens.Length == 1 && maxLen >= 5 && levScore > 0.65)
        {
            return levScore;
        }

        return (tokenScore * 0.55) + (levScore * 0.45);
    }

    private static bool IsAcronymMatch(string candidate, string[] phraseTokens)
    {
        var compactCandidate = candidate.Replace(" ", string.Empty).ToLowerInvariant();
        if (compactCandidate.Length < 2) return false;
        if (compactCandidate.Length != phraseTokens.Length) return false;

        for (int i = 0; i < phraseTokens.Length; i++)
        {
            if (phraseTokens[i].Length == 0) return false;
            if (compactCandidate[i] != char.ToLowerInvariant(phraseTokens[i][0])) return false;
        }
        return true;
    }

    private static int ComputeLevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
        if (string.IsNullOrEmpty(t)) return s.Length;

        int n = s.Length;
        int m = t.Length;
        int[,] d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }

    private static string RegexReplace(string input, string pattern, string replacement)
    {
        return Regex.Replace(input, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static DateTime NormalizeUtc(DateTime date, DateTime nowUtc)
    {
        if (date == default) return nowUtc;
        return date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime();
    }
}
