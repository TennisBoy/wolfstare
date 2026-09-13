using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace Wolfstare.Service.Api;

/// <summary>
/// Renders a random-text unlock string to a PNG so it is shown to the human but never handed to
/// a script as characters (spec §4.3 hardening). The API deliberately does not expose the text
/// as a string anywhere; a caller gets pixels, so scripting the unlock would require OCR of the
/// whole image rather than reading a JSON field.
///
/// This is not admin-proof — an administrator can still read the database where the text is
/// stored. But in the locked-down non-admin setup the database is ACL'd away, so a standard
/// user has no path to the plaintext at all.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UnlockChallengeImage
{
    private const int CharsPerLine = 80;
    private const float FontSize = 13f;
    private const int Padding = 16;

    public static byte[] RenderPng(string text)
    {
        var lines = Wrap(text, CharsPerLine);

        using var font = new Font(FontFamily.GenericMonospace, FontSize, FontStyle.Regular, GraphicsUnit.Pixel);

        // Measure with a throwaway 1x1 surface so the bitmap is sized to the content.
        SizeF lineSize;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
        {
            lineSize = pg.MeasureString(new string('m', CharsPerLine), font);
        }

        var width = (int)Math.Ceiling(lineSize.Width) + Padding * 2;
        var lineHeight = (int)Math.Ceiling(lineSize.Height);
        var height = lineHeight * lines.Count + Padding * 2;

        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(0x14, 0x16, 0x1a));
            using var brush = new SolidBrush(Color.FromArgb(0xd7, 0xdb, 0xe0));
            for (var i = 0; i < lines.Count; i++)
                g.DrawString(lines[i], font, brush, Padding, Padding + i * lineHeight);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        for (var i = 0; i < text.Length; i += width)
            lines.Add(text.Substring(i, Math.Min(width, text.Length - i)));
        return lines.Count == 0 ? [string.Empty] : lines;
    }
}
