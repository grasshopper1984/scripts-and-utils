[CmdletBinding()]
param([switch]$Preview, [string]$PreviewPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
# Original vector artwork, rendered only when maintaining the checked-in icons.
# Normal app builds embed the ICO files and need no generator or image library.
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class UtilityIcons
{
    // Every app shares one family: a filled rounded tile in its own hue with one bold white glyph.
    // Coordinates use a 32-unit canvas; sizes of 20 px and below draw a simplified, heavier glyph.
    static GraphicsPath Round(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(width, height));
        path.AddArc(x, y, d, d, 180, 90); path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90); path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
    static Color Tile(string kind)
    {
        switch (kind)
        {
            case "room": return Color.FromArgb(108, 76, 241);
            case "mouse": return Color.FromArgb(13, 148, 136);
            case "paused": return Color.FromArgb(100, 116, 139);
            case "error": return Color.FromArgb(220, 52, 64);
            default: return Color.FromArgb(234, 112, 26);
        }
    }
    static Color Shade(Color color, float amount)
    {
        Func<int, int> mix = c => (int)Math.Max(0, Math.Min(255, amount < 0 ? c * (1 + amount) : c + (255 - c) * amount));
        return Color.FromArgb(color.A, mix(color.R), mix(color.G), mix(color.B));
    }
    static void Draw(Graphics g, string kind, int pixels)
    {
        bool small = pixels <= 20;
        float unit = 32f / pixels;
        Color tile = Tile(kind);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var shape = Round(0.5f, 0.5f, 31, 31, small ? 6 : 7))
        using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(0, 32), Shade(tile, .16f), Shade(tile, -.14f)))
        using (var edge = new Pen(Shade(tile, -.38f), unit))
        { g.FillPath(fill, shape); g.DrawPath(edge, shape); }
        using (var white = new SolidBrush(Color.White))
        using (var ink = new SolidBrush(Shade(tile, -.3f)))
        {
            if (kind == "room") Room(g, white, small);
            else if (kind == "battery") Battery(g, white, ink, small);
            else Mouse(g, kind, white, ink, small, unit);
        }
    }
    static void Room(Graphics g, Brush white, bool small)
    {
        // A conversation bubble holding three participants; the amber one is the moderator.
        float x = small ? 4 : 5.5f, y = small ? 6 : 7.5f, w = small ? 24 : 21, h = small ? 16 : 14.5f;
        using (var bubble = Round(x, y, w, h, small ? 5 : 5.5f))
        {
            bubble.AddPolygon(new[] { new PointF(x + 4, y + h - 1), new PointF(x + 11, y + h - 1), new PointF(x + 3, y + h + (small ? 6.5f : 6)) });
            g.FillPath(white, bubble);
        }
        float r = small ? 2.6f : 2.1f, cy = y + h / 2, gap = small ? 6.5f : 5.6f, cx = x + w / 2;
        using (var agent = new SolidBrush(Tile("room")))
        using (var moderator = new SolidBrush(Color.FromArgb(245, 158, 11)))
        {
            g.FillEllipse(agent, cx - gap - r, cy - r, r * 2, r * 2);
            g.FillEllipse(moderator, cx - r, cy - r, r * 2, r * 2);
            g.FillEllipse(agent, cx + gap - r, cy - r, r * 2, r * 2);
        }
    }
    static void Mouse(Graphics g, string kind, Brush white, Brush ink, bool small, float unit)
    {
        // A mouse whose right button is highlighted: holding it produces a middle click.
        float x = small ? 8.5f : 9.5f, y = small ? 3.5f : 4.5f, w = small ? 15 : 13, h = small ? 25 : 23, split = y + h * .42f;
        using (var body = Round(x, y, w, h, w / 2))
        {
            g.FillPath(white, body);
            if (kind == "mouse")
            {
                var saved = g.Save(); g.SetClip(body);
                using (var button = new SolidBrush(Color.FromArgb(255, 107, 107))) g.FillRectangle(button, x + w / 2, y, w / 2, split - y);
                g.Restore(saved);
            }
        }
        using (var seam = new Pen(Shade(Tile(kind), -.3f), Math.Max(unit, small ? 1.6f : 1.2f)))
        { g.DrawLine(seam, x, split, x + w, split); g.DrawLine(seam, x + w / 2, y, x + w / 2, split); }
        using (var wheel = Round(x + w / 2 - (small ? 1.6f : 1.3f), y + (small ? 3 : 3.5f), small ? 3.2f : 2.6f, small ? 5.5f : 5, 1.3f)) g.FillPath(ink, wheel);
        float lower = split + (y + h - split) / 2;
        if (kind == "paused")
        {
            float bar = small ? 2.6f : 2, height = small ? 7 : 6.5f, spread = small ? 1.6f : 1.4f, cx = x + w / 2;
            g.FillRectangle(ink, cx - spread - bar, lower - height / 2, bar, height); g.FillRectangle(ink, cx + spread, lower - height / 2, bar, height);
        }
        if (kind == "error")
        {
            float bar = small ? 2.8f : 2.4f, cx = x + w / 2;
            g.FillRectangle(ink, cx - bar / 2, lower - (small ? 5 : 5), bar, small ? 6 : 6);
            g.FillEllipse(ink, cx - bar / 2, lower + (small ? 2.2f : 2.2f), bar, bar);
        }
    }
    static void Battery(Graphics g, Brush white, Brush ink, bool small)
    {
        // A battery with charge bars; the live percentage stays in the widget's own tray number.
        float x = small ? 3 : 4.5f, y = small ? 8.5f : 10, w = small ? 23 : 20.5f, h = small ? 15 : 12;
        using (var body = Round(x, y, w, h, small ? 3.5f : 3)) g.FillPath(white, body);
        using (var cap = Round(x + w + .5f, y + h * .3f, small ? 3.5f : 3, h * .4f, 1)) g.FillPath(white, cap);
        int bars = small ? 2 : 3; float inset = small ? 3 : 2.6f, gap = small ? 2.2f : 1.8f;
        float width = (w - inset * 2 - gap * (bars - 1)) / bars;
        for (int i = 0; i < bars; i++) g.FillRectangle(ink, x + inset + i * (width + gap), y + inset, width, h - inset * 2);
    }
    static Bitmap Render(string kind, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) { g.ScaleTransform(size / 32f, size / 32f); Draw(g, kind, size); }
        return bitmap;
    }
    public static void Save(string path, string kind)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var images = new List<byte[]>();
        foreach (int size in sizes) using (var bitmap = Render(kind, size)) using (var stream = new MemoryStream())
        { bitmap.Save(stream, ImageFormat.Png); images.Add(stream.ToArray()); }
        using (var file = File.Create(path)) using (var writer = new BinaryWriter(file))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
            }
            foreach (byte[] data in images) writer.Write(data);
        }
    }
    public static void Preview(string path)
    {
        string[] kinds = { "room", "mouse", "paused", "error", "battery" };
        string[] labels = { "Agent Room", "Right Middle", "Paused", "Input error", "Razer Battery" };
        using (var image = new Bitmap(940, 300)) using (var g = Graphics.FromImage(image))
        using (var font = new Font("Segoe UI", 10))
        {
            g.Clear(Color.FromArgb(243, 243, 243));
            using (var dark = new SolidBrush(Color.FromArgb(32, 32, 32))) g.FillRectangle(dark, 0, 150, 940, 150);
            for (int i = 0; i < kinds.Length; i++) for (int row = 0; row < 2; row++)
            {
                int x = 16 + 184 * i, y = 14 + 150 * row;
                using (var label = new SolidBrush(row == 0 ? Color.FromArgb(29, 43, 62) : Color.White)) g.DrawString(labels[i], font, label, x, y);
                int offset = 0;
                foreach (int size in new[] { 16, 20, 24, 32, 48 }) using (var icon = Render(kinds[i], size))
                { g.DrawImageUnscaled(icon, x + offset, y + 40 + (48 - size)); offset += size + 6; }
                using (var icon = Render(kinds[i], 64)) g.DrawImage(icon, x + 50, y + 94, 40, 40);
            }
            image.Save(path, ImageFormat.Png);
        }
    }
}
'@
$repoRoot = Split-Path -Parent $PSScriptRoot
[UtilityIcons]::Save((Join-Path $repoRoot 'agent-room\app.ico'), 'room')
[UtilityIcons]::Save((Join-Path $repoRoot 'right-middle\app.ico'), 'mouse')
[UtilityIcons]::Save((Join-Path $repoRoot 'right-middle\paused.ico'), 'paused')
[UtilityIcons]::Save((Join-Path $repoRoot 'right-middle\error.ico'), 'error')
[UtilityIcons]::Save((Join-Path $repoRoot 'razer-battery\app.ico'), 'battery')
if ($Preview) {
    if (-not $PreviewPath) { $PreviewPath = Join-Path $repoRoot 'agent-room\test-output\app-icons.png' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $PreviewPath) -Force | Out-Null
    [UtilityIcons]::Preview($PreviewPath)
}
