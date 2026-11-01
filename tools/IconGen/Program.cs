// Erzeugt Assets/paddock.ico (16–256 px, PNG-kodiert) aus einer Vektorzeichnung.
// Aufruf: dotnet run --project tools/IconGen -- <ausgabe.ico>
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var output = args.Length > 0 ? args[0] : "paddock.ico";
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var pngs = sizes.Select(s => (s, Render(s))).ToList();

        using var fs = File.Create(output);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)pngs.Count);
        int offset = 6 + 16 * pngs.Count;
        foreach (var (s, data) in pngs)
        {
            w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in pngs) w.Write(data);
        Console.WriteLine($"geschrieben: {output}");
    }

    static byte[] Render(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double u = size / 32.0;
            var bg = new LinearGradientBrush(Color.FromRgb(0x5B, 0x6C, 0xFF), Color.FromRgb(0x22, 0xC1, 0xC3), 45);
            dc.DrawRoundedRectangle(bg, null, new Rect(1 * u, 1 * u, 30 * u, 30 * u), 7 * u, 7 * u);
            // Glanz oben
            var gloss = new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            dc.DrawRoundedRectangle(gloss, null, new Rect(1 * u, 1 * u, 30 * u, 15 * u), 7 * u, 7 * u);
            // Zwei "Bereiche": oben breit, unten zwei Kacheln
            var panel = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
            var panelSoft = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
            dc.DrawRoundedRectangle(panel, null, new Rect(6 * u, 6.5 * u, 20 * u, 8.5 * u), 2.4 * u, 2.4 * u);
            dc.DrawRoundedRectangle(panelSoft, null, new Rect(6 * u, 17.5 * u, 8.75 * u, 8 * u), 2.4 * u, 2.4 * u);
            dc.DrawRoundedRectangle(panelSoft, null, new Rect(17.25 * u, 17.5 * u, 8.75 * u, 8 * u), 2.4 * u, 2.4 * u);
            if (size >= 24)
            {
                var dot = new SolidColorBrush(Color.FromRgb(0x5B, 0x6C, 0xFF));
                for (int i = 0; i < 3; i++)
                    dc.DrawRoundedRectangle(dot, null, new Rect((8.5 + i * 5.6) * u, 9 * u, 3.6 * u, 3.6 * u), 1 * u, 1 * u);
            }
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
