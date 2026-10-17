using System.Windows;
using System.Windows.Media;
using BACprobe.Core.Settings;
using Microsoft.Win32;

namespace BACprobe.App;

/// <summary>
/// Light, dark, or whatever Windows says, and the meaning colours (alarm, fault, override...) to match. The Fluent theme draws the
/// controls; Themes/Light.xaml or Themes/Dark.xaml supplies our colours, which must sit after the Fluent dictionary to win.
/// </summary>
internal static class ThemeManager
{
    private static ResourceDictionary? _colours;
    private static ThemeChoice _choice = ThemeChoice.System;
    private static bool _listening;
    private static System.Windows.Threading.DispatcherTimer? _settle;

    public static ThemeChoice Choice => _choice;

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        var app = Application.Current;
        if (app is null) return;
        app.ThemeMode = choice switch { ThemeChoice.Light => ThemeMode.Light, ThemeChoice.Dark => ThemeMode.Dark, _ => ThemeMode.System };

        var light = choice switch { ThemeChoice.Light => true, ThemeChoice.Dark => false, _ => WindowsUsesLightTheme() };
        var merged = app.Resources.MergedDictionaries;
        if (_colours is not null) merged.Remove(_colours);
        _colours = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(light ? "Light" : "Dark")}.xaml") };
        merged.Add(_colours); // last, so our keys (including the DataGrid selection colours) win over Fluent's
        RecolourAccent(app);
        // After a switch while running, Fluent builds some brushes (toggle buttons, checked boxes) only when they are first drawn,
        // which is after the line above, so keep recolouring for a couple of seconds. Cheap, and it stops by itself.
        _settle?.Stop();
        var ticks = 0;
        _settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _settle.Tick += (_, _) => { RecolourAccent(app); if (++ticks >= 10) _settle?.Stop(); };
        _settle.Start();

        if (!_listening)
        {
            _listening = true;
            // Windows switched light/dark while we run: follow it when the choice is System.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _choice == ThemeChoice.System)
                    app.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
            };
        }
    }

    // The app icon's blue. Its hue and saturation are used for every accent shade; each shade keeps its own lightness offset.
    private static readonly Color IconBlue = Color.FromRgb(0x0F, 0x6C, 0xBD);
    // Every resource name in Fluent's theme, read once; the ones that are accent brushes are found by their colour.
    private static IReadOnlyList<string>? _fluentNames;

    /// <summary>
    /// Fluent derives its accent brushes (buttons, checked boxes, toggles, focus lines, links) from the Windows accent colour, and
    /// the controls hold their own copies, so overriding the accent keys in a dictionary does not reach them. Find every Fluent
    /// brush that came out purple (Windows' own default accent is a blue-purple on some PCs, and a purple the user picked too)
    /// and recolour it to the icon's blue, keeping each shade's lightness so hover, pressed and text shades still differ.
    /// </summary>
    private static void RecolourAccent(Application app)
    {
        IReadOnlyList<string> names;
        try { names = _fluentNames ??= FluentResourceNames(); }
        catch (Exception) { return; } // the accent colour is only cosmetic
        var (blueHue, blueSat, blueLight) = ToHsl(IconBlue);
        foreach (var name in names)
        {
            if (app.TryFindResource(name) is not SolidColorBrush { IsFrozen: false } brush) continue;
            var (h, s, l) = ToHsl(brush.Color);
            if (s < 0.2 || h < 250 || h > 300) continue; // only the purples; Fluent's greys and reds are left alone
            // 0.427 is the lightness of Fluent's usual accent fill (#654199 for a #744DA9 accent): it lands exactly on IconBlue.
            var blue = FromHsl(blueHue, blueSat, Math.Clamp(l - 0.427 + blueLight, 0, 1), brush.Color.A);
            try { brush.Color = blue; }
            catch (Exception)
            {
                // After a theme switch some brushes still take their colour from another resource and refuse a new one:
                // put a plain brush under the same key instead, which the controls pick up the same way.
                app.Resources[name] = new SolidColorBrush(blue);
            }
        }
    }

    private static List<string> FluentResourceNames()
    {
        var asm = System.Reflection.Assembly.Load("PresentationFramework.Fluent");
        using var stream = asm.GetManifestResourceStream(asm.GetManifestResourceNames()[0])!;
        using var buffer = new System.IO.MemoryStream();
        stream.CopyTo(buffer);
        var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        return System.Text.RegularExpressions.Regex.Matches(text, "[A-Z][A-Za-z0-9]{5,}").Select(m => m.Value).Distinct().ToList();
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);
        var s = d / (1 - Math.Abs(2 * l - 1));
        var h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, s, l);
    }

    private static Color FromHsl(double h, double s, double l, byte alpha)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return Color.FromArgb(alpha, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception) { return true; }
    }
}
