using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BatteryTray;

/// <summary>
/// Pixel-drawn 16x16 tray icon: bold digits on top, device glyph and level bar below.
/// GDI+ text blurs at 16 px, so every pixel is placed by hand and scaled by an integer factor.
/// </summary>
public static class IconPainter
{
    private const int Base = 16;

    // 6x10 digits with 2 px strokes; "1" is 2 px wide so that "100" fits in 16 px.
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = new[] { ".####.", "######", "##..##", "##..##", "##..##", "##..##", "##..##", "##..##", "######", ".####." },
        ['1'] = new[] { "##", "##", "##", "##", "##", "##", "##", "##", "##", "##" },
        ['2'] = new[] { ".####.", "######", "....##", "....##", "######", "######", "##....", "##....", "######", "######" },
        ['3'] = new[] { ".####.", "######", "....##", "....##", ".#####", ".#####", "....##", "....##", "######", ".####." },
        ['4'] = new[] { "##..##", "##..##", "##..##", "##..##", "######", "######", "....##", "....##", "....##", "....##" },
        ['5'] = new[] { "######", "######", "##....", "##....", "#####.", "######", "....##", "....##", "######", ".####." },
        ['6'] = new[] { ".####.", "######", "##....", "##....", "#####.", "######", "##..##", "##..##", "######", ".####." },
        ['7'] = new[] { "######", "######", "....##", "....##", "....##", "....##", "....##", "....##", "....##", "....##" },
        ['8'] = new[] { ".####.", "######", "##..##", "##..##", ".####.", "######", "##..##", "##..##", "######", ".####." },
        ['9'] = new[] { ".####.", "######", "##..##", "##..##", "######", ".#####", "....##", "....##", "######", ".####." },
        ['-'] = new[] { "....", "....", "....", "....", "####", "####", "....", "....", "....", "...." },
    };

    // Status glyphs for the 16x10 digit area: big Z with a small z above it, and the power symbol.
    private static readonly string[] Sleep =
    {
        ".........#####", "...........##.", "#######..##...", "#######.#####.", "....###.......",
        "...###........", "..###.........", ".###..........", "#######.......", "#######.......",
    };
    private static readonly string[] Power =
    {
        "....##....", "..#.##.#..", ".##.##.##.", "##..##..##", "##..##..##",
        "##......##", "##......##", ".##....##.", "..######..", "...####...",
    };

    private static readonly string[] Headset = { ".###.", "#...#", "##.##", "##.##" };
    private static readonly string[] Mouse = { ".##.", "####", "####", ".##." };

    private static readonly Color Green = Color.FromArgb(76, 217, 100), Amber = Color.FromArgb(255, 176, 46),
        Red = Color.FromArgb(255, 82, 82), Cyan = Color.FromArgb(64, 200, 255), Grey = Color.FromArgb(128, 128, 128);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    public static void Destroy(Icon icon) { DestroyIcon(icon.Handle); icon.Dispose(); }

    /// <param name="phase">Animation step for the charging bar, 0..3.</param>
    /// <param name="lastPercent">Last known level, drawn grey in the bar while the device gives none.</param>
    public static Icon Draw(BatteryState s, bool headset, int phase = 0, int? lastPercent = null, int size = 0)
    {
        if (size <= 0) size = SystemInformation.SmallIconSize.Width;
        bool light = IsLightTaskbar();
        Color ink = light ? Color.FromArgb(26, 26, 26) : Color.White;
        Color dim = Color.FromArgb(light ? 70 : 80, ink);
        Color sleepInk = light ? Color.FromArgb(80, 90, 150) : Color.FromArgb(160, 170, 215);

        using var px = new Bitmap(Base, Base);
        int? p = s.Percent;

        if (p is int v)
        {
            // Digits: red/amber only when the level is low, so the normal state stays calm.
            Color digitColor = v switch { <= 15 when !s.Charging => Red, <= 30 when !s.Charging => Amber, _ => ink };
            string text = v.ToString();
            int width = text.Sum(c => Glyphs[c][0].Length) + text.Length - 1;
            int x = (Base - width + 1) / 2;
            foreach (char c in text)
            {
                Blit(px, Glyphs[c], x, 0, digitColor);
                x += Glyphs[c][0].Length + 1;
            }
        }
        else if (s.Presence == Presence.Asleep) Blit(px, Sleep, 1, 0, sleepInk);
        else if (s.Presence == Presence.Off) Blit(px, Power, 3, 0, Grey);
        else { Blit(px, Glyphs['-'], 3, 0, Grey); Blit(px, Glyphs['-'], 9, 0, Grey); }

        // Device glyph bottom-left, level bar bottom-right.
        Blit(px, headset ? Headset : Mouse, 0, 12, p is null ? Grey : Color.FromArgb(200, ink));
        const int barX = 6, barW = Base - barX, barY = 13;
        Color barColor = s.Charging ? Cyan : p switch { null => Grey, <= 15 => Red, <= 30 => Amber, _ => Green };
        int? shown = p ?? lastPercent;
        int fill = shown is int f ? Math.Max(1, (int)Math.Round(barW * f / 100.0)) : 0;
        // Charging: the bar grows from the real level to full in four steps.
        if (s.Charging && p is not null) fill += (barW - fill) * (phase % 4) / 3;
        for (int i = 0; i < barW; i++)
        for (int j = 0; j < 2; j++)
            px.SetPixel(barX + i, barY + j, i < fill ? barColor : dim);

        return ToIcon(px, size);
    }

    private static void Blit(Bitmap bmp, string[] glyph, int x, int y, Color c)
    {
        for (int r = 0; r < glyph.Length; r++)
        for (int k = 0; k < glyph[r].Length; k++)
            if (glyph[r][k] == '#' && x + k is >= 0 and < Base && y + r is >= 0 and < Base)
                bmp.SetPixel(x + k, y + r, c);
    }

    /// <summary>Integer nearest-neighbour scale keeps edges sharp at 125-200 % DPI.</summary>
    private static Icon ToIcon(Bitmap px, int size)
    {
        int k = Math.Max(1, size / Base), off = (size - Base * k) / 2;
        using var bmp = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(px, off, off, Base * k, Base * k);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static bool IsLightTaskbar()
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }
}
