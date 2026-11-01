using System.Runtime.InteropServices;
using System.Windows.Media;

namespace Paddock.Interop;

public enum BackdropKind
{
    /// <summary>Acrylglas: Unschärfe + Rauschen, wie Windows-Flyouts.</summary>
    Acrylic,
    /// <summary>Reine Unschärfe ohne Acryl-Textur.</summary>
    Blur,
    /// <summary>Nur halbtransparente Farbe, keine Unschärfe – am sparsamsten.</summary>
    Solid,
}

public enum CornerKind { Round, Small, Square }

/// <summary>
/// Hintergrund-Effekte über DWM. Die Fenster sind bewusst nicht "layered" –
/// DWM komponiert Unschärfe & Transparenz auf der GPU, WPF zeichnet nur den Inhalt.
/// </summary>
public static class Backdrop
{
    const int WCA_ACCENT_POLICY = 19;
    const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2, ACCENT_ENABLE_BLURBEHIND = 3, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    public static void Apply(IntPtr hwnd, BackdropKind kind, Color tint, bool dark, CornerKind corners, Color? border)
    {
        try
        {
            int darkFlag = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, 4);

            int corner = corners switch { CornerKind.Round => 2, CornerKind.Small => 3, _ => 1 };
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);

            // DWM-Rahmen ist deckend → Theme-Rahmenfarbe (mit Alpha) auf die Tönung mischen
            int borderColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            if (border is { } b && b.A > 0)
            {
                double a = Math.Clamp(b.A / 255.0 * 1.6, 0, 1);
                byte Mix(byte t, byte c) => (byte)Math.Round(t * (1 - a) + c * a);
                borderColor = Mix(tint.R, b.R) | (Mix(tint.G, b.G) << 8) | (Mix(tint.B, b.B) << 16);
            }
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref borderColor, 4);

            // Keine Fenster-Animationen von DWM (Ein-/Ausblenden machen wir selbst, weicher).
            int noTransitions = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref noTransitions, 4);
            // Wichtig: KEIN DwmExtendFrameIntoClientArea – sonst füllt DWM die Fläche deckend statt mit dem Akzent.
        }
        catch { /* ältere Windows-Versionen kennen manche Attribute nicht */ }

        // Die Tönung zeichnet WPF; der Akzent liefert nur Unschärfe bzw. Transparenz
        // (Alpha 1 statt 0, sonst Fehlverhalten unter Windows 10).
        int state = kind switch
        {
            BackdropKind.Acrylic => ACCENT_ENABLE_ACRYLICBLURBEHIND,
            BackdropKind.Blur => ACCENT_ENABLE_BLURBEHIND,
            _ => ACCENT_ENABLE_TRANSPARENTGRADIENT,
        };
        uint gradient = (0x01u << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R;
        SetAccent(hwnd, state, gradient);
    }

    static void SetAccent(IntPtr hwnd, int state, uint gradient)
    {
        var accent = new AccentPolicy { AccentState = state, AccentFlags = state == ACCENT_ENABLE_TRANSPARENTGRADIENT ? 2 : 0, GradientColor = gradient };
        int size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch { }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>Mica für normale App-Fenster (Einstellungen); fällt still zurück, wo nicht verfügbar.</summary>
    public static void ApplyMica(IntPtr hwnd, bool dark)
    {
        try
        {
            int darkFlag = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, 4);
            int mica = 2; // DWMSBT_MAINWINDOW
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref mica, 4);
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch { }
    }

    public static void ApplyDarkTitleBar(IntPtr hwnd, bool dark)
    {
        try
        {
            int darkFlag = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, 4);
        }
        catch { }
    }
}
