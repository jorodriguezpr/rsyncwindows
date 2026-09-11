// rsyncWindows
// Developer: Jose Rodriguez Arroyo
// Email: jrpcone@gmail.com
// GitHub: https://github.com/jorodriguezpr
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Generates dist\icon.ico: a simple two-arrow "sync" glyph (dark blue circle, white arrows),
// multi-resolution (16/32/48/256), PNG-compressed entries (Vista+ ICO format -- simplest to
// write correctly without hand-rolling BMP/AND-mask encoding).
int[] sizes = [16, 32, 48, 256];
var pngs = new List<byte[]>();

foreach (int size in sizes)
{
    using var bmp = new Bitmap(size, size);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float pad = size * 0.06f;
        var circleRect = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
        using (var bg = new LinearGradientBrush(circleRect, Color.FromArgb(255, 24, 90, 189), Color.FromArgb(255, 13, 58, 128), 45f))
            g.FillEllipse(bg, circleRect);

        float cx = size / 2f, cy = size / 2f;
        float r = size * 0.30f;
        float thickness = Math.Max(1.5f, size * 0.085f);
        using var pen = new Pen(Color.White, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Flat };

        // Top arc + arrowhead (pointing right), bottom arc + arrowhead (pointing left) --
        // classic two-arrow refresh/sync glyph.
        var topArc = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        g.DrawArc(pen, topArc, 200, 150);
        var bottomArc = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        g.DrawArc(pen, bottomArc, 20, 150);

        double topEndAngle = (200 + 150) * Math.PI / 180.0;
        float tipX = cx + r * (float)Math.Cos(topEndAngle);
        float tipY = cy + r * (float)Math.Sin(topEndAngle);
        DrawArrowhead(g, tipX, tipY, (float)(topEndAngle + Math.PI / 2), size * 0.14f, Color.White);

        double botEndAngle = (20 + 150) * Math.PI / 180.0;
        float tip2X = cx + r * (float)Math.Cos(botEndAngle);
        float tip2Y = cy + r * (float)Math.Sin(botEndAngle);
        DrawArrowhead(g, tip2X, tip2Y, (float)(botEndAngle + Math.PI / 2), size * 0.14f, Color.White);
    }

    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    pngs.Add(ms.ToArray());
    bmp.Save(Path.Combine(Path.GetTempPath(), $"icon_preview_{size}.png"), ImageFormat.Png);
}

string outPath = args.Length > 0 ? args[0] : "icon.ico";
using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort)0); // reserved
    w.Write((ushort)1); // type = icon
    w.Write((ushort)sizes.Length);

    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        int s = sizes[i];
        w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); // color count
        w.Write((byte)0); // reserved
        w.Write((ushort)1); // planes
        w.Write((ushort)32); // bit count
        w.Write((uint)pngs[i].Length);
        w.Write((uint)offset);
        offset += pngs[i].Length;
    }
    foreach (var png in pngs)
        w.Write(png);
}

Console.WriteLine($"Wrote {outPath} ({sizes.Length} sizes: {string.Join(",", sizes)})");
return;

static void DrawArrowhead(Graphics g, float tipX, float tipY, float direction, float size, Color color)
{
    using var brush = new SolidBrush(color);
    var p1 = new PointF(tipX, tipY);
    var p2 = new PointF(
        tipX - size * (float)Math.Cos(direction - 0.5f),
        tipY - size * (float)Math.Sin(direction - 0.5f));
    var p3 = new PointF(
        tipX - size * (float)Math.Cos(direction + 0.5f),
        tipY - size * (float)Math.Sin(direction + 0.5f));
    g.FillPolygon(brush, [p1, p2, p3]);
}
