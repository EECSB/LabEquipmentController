using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LabEquipmentController;

/// <summary>
/// Loads the embedded button glyphs (icons.&lt;name&gt;.png, 64×64 with alpha) and
/// downscales them to a requested pixel size, cached. Kept in the app (not Core)
/// because it depends on System.Drawing / WinForms.
/// </summary>
internal static class AppIcons
{
    private static readonly Dictionary<(string Name, int Px), Image> _cache = new();

    /// <summary>Get a glyph scaled to <paramref name="px"/>×<paramref name="px"/> device pixels, or null if missing.</summary>
    public static Image? Get(string name, int px)
    {
        px = Math.Max(8, px);
        var key = (name, px);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream("icons." + name + ".png");
            if (stream == null) return null;

            using var src = new Bitmap(stream);
            var dst = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, px, px));
            }
            _cache[key] = dst;
            return dst;
        }
        catch
        {
            return null;   // a missing/corrupt glyph must never break the UI
        }
    }

    // ------------------------------------------------------------------ drawn glyphs

    private static readonly Dictionary<(string Name, int Px), Image> _drawn = new();

    /// <summary>Line weight and colour to match the bundled artwork.</summary>
    private static readonly Color GlyphInk = Color.FromArgb(40, 40, 44);

    /// <summary>
    /// Glyphs drawn at runtime instead of loaded from a PNG, for the actions with no bundled
    /// artwork. Drawn straight to the requested pixel size, so they stay sharp at any DPI
    /// rather than being downscaled like the embedded bitmaps.
    ///
    /// Known names: "detach", "attach", "search", "camera", "wave", "bars", "copy", "insert".
    /// </summary>
    public static Image? Drawn(string name, int px)
    {
        px = Math.Max(8, px);
        var key = (name, px);
        if (_drawn.TryGetValue(key, out var cached)) return cached;

        try
        {
            var dst = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.ScaleTransform(px / 64f, px / 64f);   // author everything in a 64x64 space

                switch (name)
                {
                    case "detach": DrawMove(g, outward: true); break;
                    case "attach": DrawMove(g, outward: false); break;
                    case "search": DrawSearch(g); break;
                    case "camera": DrawCamera(g); break;
                    case "wave": DrawWave(g); break;
                    case "bars": DrawBars(g); break;
                    case "copy": DrawCopy(g); break;
                    case "insert": DrawInsert(g); break;
                    case "ai": DrawSparkle(g); break;
                    case "save": DrawFloppy(g); break;
                    case "folder": DrawFolder(g); break;
                    case "globe": DrawGlobe(g); break;
                    default: return null;
                }
            }
            _drawn[key] = dst;
            return dst;
        }
        catch
        {
            return null;   // cosmetic only, exactly like the embedded glyphs
        }
    }

    /// <summary>A pen in the glyph ink, with round ends so short strokes don't look chopped.</summary>
    private static Pen Stroke(float width = 7f) => new(GlyphInk, width)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

    /// <summary>A magnifier — asking the instrument what commands it knows.</summary>
    private static void DrawSearch(Graphics g)
    {
        using Pen p = Stroke();
        g.DrawEllipse(p, 10, 10, 32, 32);
        g.DrawLine(p, 39, 39, 55, 55);
    }

    /// <summary>
    /// A globe, for a button that leaves the app for the web. The magnifier this replaced
    /// said "search" — which is what the button does to find the page, not what happens when
    /// you press it, and search is what the filter box above does.
    ///
    /// Outline, equator, and one meridian ellipse squeezed to a third of the width: that
    /// third arc is what reads as a sphere rather than a circle with a line through it.
    /// </summary>
    private static void DrawGlobe(Graphics g)
    {
        using Pen p = Stroke();
        g.DrawEllipse(p, 8, 8, 48, 48);          // the sphere
        g.DrawLine(p, 8, 32, 56, 32);            // equator
        g.DrawEllipse(p, 24, 8, 16, 48);         // one meridian, seen edge-on
    }

    /// <summary>
    /// A camera for the screen grab: an outlined body with a solid lens. Solid body with a
    /// hollow lens would need to paint the button face back in, and that colour is the
    /// theme's to choose, not ours.
    /// </summary>
    private static void DrawCamera(Graphics g)
    {
        using Pen p = Stroke(6f);
        using var fill = new SolidBrush(GlyphInk);

        g.FillRectangle(fill, 22, 10, 20, 9);      // the viewfinder hump
        g.DrawRectangle(p, 7, 19, 50, 36);         // body
        g.FillEllipse(fill, 23, 28, 18, 18);       // lens
    }

    /// <summary>One cycle of a sine, for capturing a trace.</summary>
    private static void DrawWave(Graphics g)
    {
        using Pen p = Stroke();
        g.DrawCurve(p, new[]
        {
            new PointF(7, 32), new PointF(19, 13), new PointF(32, 32),
            new PointF(45, 51), new PointF(57, 32),
        }, 0.5f);
    }

    /// <summary>
    /// Three bars of different heights — readings taken one after another — for the live
    /// readout.
    ///
    /// The obvious drawing is a meter's dial and needle, and two attempts at one both failed
    /// at the size this actually renders: the arc and the needle either merged into a single
    /// thick eyebrow or, drawn apart, read as a scribble. Bars have no fine structure to lose.
    /// They also stay distinct from <see cref="DrawWave"/> on the button beside them, which a
    /// trend line would not — two squiggles 16 pixels wide are one squiggle.
    /// </summary>
    private static void DrawBars(Graphics g)
    {
        using var fill = new SolidBrush(GlyphInk);
        g.FillRectangle(fill, 8, 34, 13, 22);
        g.FillRectangle(fill, 26, 18, 13, 38);
        g.FillRectangle(fill, 44, 27, 13, 29);
    }

    /// <summary>
    /// Two sheets, the front one whole and the back one showing only the corner that clears
    /// it — the usual copy mark, and the only way to suggest a stack without painting over
    /// the button face.
    /// </summary>
    private static void DrawCopy(Graphics g)
    {
        using Pen p = Stroke(6f);
        g.DrawLines(p, new[] { new PointF(22, 10), new PointF(54, 10), new PointF(54, 42) });
        g.DrawRectangle(p, 10, 22, 32, 32);
    }

    /// <summary>
    /// A floppy disk: the save mark.
    ///
    /// The bundled "saveFile" glyph is a page with a downward arrow, which means *download* —
    /// fine for exporting a list, wrong on a Save button, where it tells the user the file is
    /// coming from somewhere rather than going to disk.
    ///
    /// Body and shutter only. A real floppy also has a label panel, and at the size this
    /// renders a third rectangle inside the other two is just a darker smudge.
    /// </summary>
    private static void DrawFloppy(Graphics g)
    {
        using Pen p = Stroke(6f);
        using var fill = new SolidBrush(GlyphInk);

        // The clipped top-right corner is what makes it a floppy rather than a plain square.
        g.DrawLines(p, new[]
        {
            new PointF(10, 10), new PointF(44, 10), new PointF(54, 20),
            new PointF(54, 54), new PointF(10, 54), new PointF(10, 10),
        });
        g.FillRectangle(fill, 22, 14, 18, 16);   // the metal shutter
    }

    /// <summary>
    /// A folder, for Browse. A magnifier says "search for something in here"; picking a file
    /// off disk is opening a folder, and the two are not the same gesture.
    /// </summary>
    private static void DrawFolder(Graphics g)
    {
        using Pen p = Stroke(6f);
        g.DrawLines(p, new[]
        {
            new PointF(8, 50), new PointF(8, 16), new PointF(26, 16),
            new PointF(33, 25), new PointF(56, 25), new PointF(56, 50),
            new PointF(8, 50),
        });
    }

    /// <summary>
    /// A large sparkle with a small one beside it — the mark that has come to mean "a model
    /// did this", and the only AI symbol that survives being 16 pixels wide. A chip or a
    /// brain at this size is a grey blob; four sharp points are still four sharp points.
    ///
    /// The pair matters: one star alone reads as a rating or a favourite, and it is the
    /// second, smaller one that makes it a sparkle.
    /// </summary>
    private static void DrawSparkle(Graphics g)
    {
        using var fill = new SolidBrush(GlyphInk);
        Star(g, fill, cx: 25, cy: 36, r: 25);
        Star(g, fill, cx: 49, cy: 13, r: 12);
    }

    /// <summary>
    /// A four-pointed star: tips on the axes, waists pulled in to 28% of the radius. Sharper
    /// than that and the points break up at small sizes; blunter and it stops being a
    /// sparkle and starts being a diamond.
    /// </summary>
    private static void Star(Graphics g, Brush fill, float cx, float cy, float r)
    {
        float w = r * 0.28f;
        g.FillPolygon(fill, new[]
        {
            new PointF(cx, cy - r), new PointF(cx + w, cy - w),
            new PointF(cx + r, cy), new PointF(cx + w, cy + w),
            new PointF(cx, cy + r), new PointF(cx - w, cy + w),
            new PointF(cx - r, cy), new PointF(cx - w, cy - w),
        });
    }

    /// <summary>An arrow to the right: the command moving into the command box.</summary>
    private static void DrawInsert(Graphics g)
    {
        using Pen p = Stroke();
        using var fill = new SolidBrush(GlyphInk);

        g.DrawLine(p, 9, 32, 36, 32);
        g.FillPolygon(fill, new[]
        {
            new PointF(57, 32), new PointF(35, 20), new PointF(35, 44),
        });
    }

    /// <summary>
    /// A bold diagonal arrow: out to the top right for detach, back to the bottom left for
    /// re-attach.
    ///
    /// Deliberately just an arrow. Earlier versions drew the console's window as a box with
    /// the arrow entering or leaving it, but these render at about 16 device pixels, where
    /// a box plus a shaft plus a head is a smudge — the inbound one read as a flag. The
    /// direction is the whole message, and the button's label supplies the rest.
    /// </summary>
    private static void DrawMove(Graphics g, bool outward)
    {
        using var shaft = new Pen(GlyphInk, 7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new SolidBrush(GlyphInk);

        if (outward)
        {
            g.DrawLine(shaft, 14, 50, 40, 24);
            g.FillPolygon(fill, new[]
            {
                new PointF(56, 8), new PointF(48, 32), new PointF(32, 16),
            });
        }
        else
        {
            g.DrawLine(shaft, 50, 14, 24, 40);
            g.FillPolygon(fill, new[]
            {
                new PointF(8, 56), new PointF(32, 48), new PointF(16, 32),
            });
        }
    }
}
