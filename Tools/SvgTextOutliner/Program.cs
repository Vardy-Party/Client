using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

// MAUI Resizetizer (11 RC2) throws MissingMethodException while shaping SVG
// <text> on Windows, iOS, and Mac Catalyst. Outlines keep the same label
// without sending the resizetizer down that path. See dotnet/maui#38507.
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: SvgTextOutliner <svg-path>");
    return 1;
}

var svgPath = args[0];
var svg = File.ReadAllText(svgPath);
var replaced = SvgTextOutliner.ReplaceTextElements(svg);
if (!string.Equals(svg, replaced, StringComparison.Ordinal))
    File.WriteAllText(svgPath, replaced);

return 0;

internal static partial class SvgTextOutliner
{
    public static string ReplaceTextElements(string svg) =>
        TextElementRegex().Replace(svg, match => Outline(match.Groups[1].Value, match.Groups[2].Value));

    private static string Outline(string attrs, string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var x = ParseFloat(Required(attrs, "x"));
        var y = ParseFloat(Required(attrs, "y"));
        var size = ParseFloat(Required(attrs, "font-size").TrimEnd('p', 'x', 'P', 'X'));
        var fill = Required(attrs, "fill");
        var anchor = Attr(attrs, "text-anchor") ?? "start";
        var weight = int.TryParse(Attr(attrs, "font-weight"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 400;

        using var typeface = LoadBundledTypeface(weight);
        using var font = new SKFont(typeface, size);
        var advance = font.MeasureText(text);
        var originX = anchor switch
        {
            "middle" => x - (advance / 2f),
            "end" => x - advance,
            _ => x,
        };
        using var path = font.GetTextPath(text, new SKPoint(originX, y));
        if (path is null || path.IsEmpty)
            throw new InvalidOperationException($"Font '{typeface.FamilyName}' produced an empty outline for '{text}'.");

        var data = path.ToSvgPathData();
        return $"<path d='{data}' fill='{fill}'/>";
    }

    private static SKTypeface LoadBundledTypeface(int weight)
    {
        // Liberation Sans has Regular and Bold only. Weight 600 (the splash
        // version line) uses Bold. Host fonts are ignored: GitHub runners do
        // not have Segoe UI or Arial.
        var fileName = weight >= 600 ? "LiberationSans-Bold.ttf" : "LiberationSans-Regular.ttf";
        var path = Path.Combine(AppContext.BaseDirectory, "Fonts", fileName);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Bundled font was not copied next to the tool: {path}");

        return SKTypeface.FromFile(path)
            ?? throw new InvalidOperationException($"Skia could not read the bundled font '{path}'.");
    }

    private static string Required(string attrs, string name) =>
        Attr(attrs, name) ?? throw new InvalidOperationException($"SVG text is missing '{name}'.");

    private static string? Attr(string attrs, string name)
    {
        var match = Regex.Match(
            attrs,
            $@"(?:^|\s){Regex.Escape(name)}\s*=\s*(?:'([^']*)'|""([^""]*)"")",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    private static float ParseFloat(string value) =>
        float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"<text\b([^>]*)>([^<]*)</text>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TextElementRegex();
}
