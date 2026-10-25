# Builds the program icon (.ico with all the standard sizes 16..256, and a 256 px .png) from a picture.
# White around the drawing (connected to the edge of the picture) becomes transparent.
#   powershell -STA -File tools\MakeIcon.ps1 -Source BudzetDomowy.App\Assets\icon-source.webp
# Must stay plain ASCII (Windows PowerShell 5.1).
param(
    [string]$Source = "",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if ($Source -eq "") { $Source = Join-Path $root "BudzetDomowy.App\Assets\icon-source.webp" }
if ($OutDir -eq "") { $OutDir = Join-Path $root "BudzetDomowy.App\Assets" }
if (-not (Test-Path $Source)) { throw "Source picture not found: $Source" }
if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne "STA") { throw "Run with: powershell -STA -File tools\MakeIcon.ps1" }

Add-Type -ReferencedAssemblies PresentationCore, WindowsBase, System.Xaml -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class IconMaker
{
    // standard icon sizes
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    public static void Make(string source, string icoPath, string pngPath)
    {
        BitmapSource src;
        using (var fs = File.OpenRead(source))
            src = new FormatConvertedBitmap(BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);

        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);

        // 1. the white that touches the edge of the picture is background
        var bg = new bool[w * h];
        var queue = new Queue<int>();
        Action<int, int> seed = (x, y) => { int i = y * w + x; if (!bg[i] && IsWhite(px, i)) { bg[i] = true; queue.Enqueue(i); } };
        for (int x = 0; x < w; x++) { seed(x, 0); seed(x, h - 1); }
        for (int y = 0; y < h; y++) { seed(0, y); seed(w - 1, y); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(); int x = i % w, y = i / w;
            if (x > 0) seed(x - 1, y);
            if (x < w - 1) seed(x + 1, y);
            if (y > 0) seed(x, y - 1);
            if (y < h - 1) seed(x, y + 1);
        }

        // 2. the drawing shrinks by two pixels (the light fringe of the edge goes away), then the edge is softened
        var alpha = new float[w * h];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = bg[i] ? 0f : 1f;
        for (int pass = 0; pass < 2; pass++)
        {
            var next = (float[])alpha.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (alpha[i] == 0f) continue;
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1 || alpha[i - 1] == 0f || alpha[i + 1] == 0f || alpha[i - w] == 0f || alpha[i + w] == 0f) next[i] = 0f;
                }
            alpha = next;
        }
        alpha = Blur(alpha, w, h);

        // 3. crop to the drawing, add a little room, make it square
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (alpha[y * w + x] > 0.02f) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        int side = (int)(Math.Max(cw, ch) * 1.06);
        var square = new byte[side * side * 4];
        int offX = (side - cw) / 2, offY = (side - ch) / 2;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int si = (y + minY) * w + (x + minX);
                int di = ((y + offY) * side + (x + offX)) * 4;
                float a = alpha[si];
                // premultiplied colour, as WPF scales it
                square[di] = (byte)(px[si * 4] * a);
                square[di + 1] = (byte)(px[si * 4 + 1] * a);
                square[di + 2] = (byte)(px[si * 4 + 2] * a);
                square[di + 3] = (byte)(a * 255f);
            }
        var big = BitmapSource.Create(side, side, 96, 96, PixelFormats.Pbgra32, null, square, side * 4);

        // 4. every size from a 256 px master (never more than a 16:1 step)
        var master = Scale(big, 256);
        var frames = new List<KeyValuePair<int, byte[]>>();
        foreach (int size in Sizes)
        {
            var frame = size == 256 ? master : Scale(master, size);
            var bgra = new byte[size * size * 4];
            frame.CopyPixels(bgra, size * 4, 0);
            Unpremultiply(bgra);
            frames.Add(new KeyValuePair<int, byte[]>(size, size >= 64 ? Png(size, bgra) : Dib(size, bgra)));
        }

        using (var o = File.Create(icoPath))
        using (var bw = new BinaryWriter(o))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)frames.Count);
            int offset = 6 + 16 * frames.Count;
            foreach (var f in frames)
            {
                bw.Write((byte)(f.Key == 256 ? 0 : f.Key)); bw.Write((byte)(f.Key == 256 ? 0 : f.Key));
                bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
                bw.Write(f.Value.Length); bw.Write(offset);
                offset += f.Value.Length;
            }
            foreach (var f in frames) bw.Write(f.Value);
        }

        var pngEncoder = new PngBitmapEncoder();
        pngEncoder.Frames.Add(BitmapFrame.Create(master));
        using (var p = File.Create(pngPath)) pngEncoder.Save(p);
    }

    static bool IsWhite(byte[] px, int i) { int o = i * 4; return px[o] >= 246 && px[o + 1] >= 246 && px[o + 2] >= 246; }

    static float[] Blur(float[] a, int w, int h)
    {
        var r = new float[a.Length];
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                float s = 0;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) s += a[(y + dy) * w + x + dx];
                r[y * w + x] = s / 9f;
            }
        return r;
    }

    static BitmapSource Scale(BitmapSource s, int size)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);
        using (var dc = visual.RenderOpen()) dc.DrawImage(s, new Rect(0, 0, size, size));
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    static void Unpremultiply(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 0 || a == 255) continue;
            bgra[i] = (byte)Math.Min(255, bgra[i] * 255 / a);
            bgra[i + 1] = (byte)Math.Min(255, bgra[i + 1] * 255 / a);
            bgra[i + 2] = (byte)Math.Min(255, bgra[i + 2] * 255 / a);
        }
    }

    static byte[] Png(int size, byte[] bgra)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bgra, size * 4)));
        using (var m = new MemoryStream()) { enc.Save(m); return m.ToArray(); }
    }

    // the classic icon frame: a DIB with the colours bottom-up, and a (here empty) 1-bit mask
    static byte[] Dib(int size, byte[] bgra)
    {
        int maskRow = ((size + 31) / 32) * 4;
        using (var m = new MemoryStream())
        using (var bw = new BinaryWriter(m))
        {
            bw.Write(40); bw.Write(size); bw.Write(size * 2); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(size * size * 4 + maskRow * size); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = size - 1; y >= 0; y--) bw.Write(bgra, y * size * 4, size * 4);
            bw.Write(new byte[maskRow * size]);
            return m.ToArray();
        }
    }
}
"@

New-Item -ItemType Directory -Force $OutDir | Out-Null
$ico = Join-Path $OutDir "BudzetDomowy.ico"
$png = Join-Path $OutDir "BudzetDomowy.png"
[IconMaker]::Make((Resolve-Path $Source).Path, $ico, $png)
Write-Host "Wrote $ico and $png"
