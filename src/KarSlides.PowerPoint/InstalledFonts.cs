using System.Runtime.InteropServices;

namespace KarSlides.PowerPoint;

/// <summary>
/// Fonts installed in Windows (machine and per-user), as GDI sees them: family names ("Open Sans Medium"
/// is its own GDI family) and full names. Compared with PowerPoint's Presentation.Fonts names.
/// </summary>
public static unsafe class InstalledFonts
{
    public static HashSet<string> Get()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var lf = new LOGFONTW { lfCharSet = DEFAULT_CHARSET };
            EnumFontFamExProc proc = (elfe, _, _, _) =>
            {
                names.Add(new string(elfe->elfLogFont.lfFaceName).TrimEnd('\0'));
                names.Add(new string(elfe->elfFullName).TrimEnd('\0'));
                return 1;
            };
            EnumFontFamiliesExW(dc, ref lf, proc, IntPtr.Zero, 0);
            GC.KeepAlive(proc);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        names.Remove("");
        return names;
    }

    private const byte DEFAULT_CHARSET = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONTW
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        public fixed char lfFaceName[32];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUMLOGFONTEXW
    {
        public LOGFONTW elfLogFont;
        public fixed char elfFullName[64];
        public fixed char elfStyle[32];
        public fixed char elfScript[32];
    }

    private delegate int EnumFontFamExProc(ENUMLOGFONTEXW* lpelfe, IntPtr lpntme, uint fontType, IntPtr lParam);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesExW(IntPtr hdc, ref LOGFONTW lpLogfont, EnumFontFamExProc proc, IntPtr lParam, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
