using System.Text.RegularExpressions;

namespace NoorVox.Tests;

/// <summary>WCAG 2.1 AA contrast (4.5:1 for normal text) for every theme, on the colour pairs the studio actually uses.</summary>
public partial class AccessibilityTests
{
    static readonly (string Fg, string Bg)[] Pairs =
    [
        ("text", "bg"), ("text", "surface"), ("text", "surface-2"),
        ("text-muted", "bg"), ("text-muted", "surface"), ("text-muted", "surface-2"),
        ("on-accent", "accent"), ("accent-strong", "surface"),
        ("ok", "surface"), ("warn", "surface"), ("err", "surface"), ("err", "surface-2"),
    ];

    [GeneratedRegex("""(?:\:root, )?\[data-theme="(\w+)"\]\s*\{([^}]*)\}""")]
    private static partial Regex ThemeBlock();

    [GeneratedRegex(@"--([\w-]+):\s*(#[0-9a-fA-F]{6})")]
    private static partial Regex Token();

    static double Luminance(string hex)
    {
        double C(int i)
        {
            var c = Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * C(0) + 0.7152 * C(1) + 0.0722 * C(2);
    }

    static double Ratio(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void Every_theme_meets_AA_contrast()
    {
        var css = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "NoorVox.Studio.Web", "src", "styles.css"));
        var themes = ThemeBlock().Matches(css).ToDictionary(m => m.Groups[1].Value, m => Token().Matches(m.Groups[2].Value).ToDictionary(t => t.Groups[1].Value, t => t.Groups[2].Value));
        Assert.Equal(["nocturne", "daylight", "tape", "thalassa", "graphite", "lavender"], themes.Keys);
        var failures = (from t in themes from p in Pairs
                        let r = Ratio(t.Value[p.Fg], t.Value[p.Bg])
                        where r < 4.5
                        select $"{t.Key}: {p.Fg} on {p.Bg} = {r:0.00}").ToList();
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    public void Reduced_motion_is_respected()
    {
        var css = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "NoorVox.Studio.Web", "src", "styles.css"));
        Assert.Contains("prefers-reduced-motion: reduce", css);
    }
}
