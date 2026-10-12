// LvTextFile.cs - the one reader and writer for .lvproj, .lvclass and .lvlib text, so an edit
// keeps the BOM and line endings LabVIEW saved the file with.
using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Reading and writing a LabVIEW project-tree text file - <c>.lvproj</c>, <c>.lvclass</c>,
/// <c>.lvlib</c> - without changing its BYTE STYLE.
///
/// WHY THIS EXISTS. LabVIEW saves these files with a UTF-8 BOM (<c>EF BB BF</c>) and CRLF line
/// endings - read 2026-10-12 off about eighty project, class and library files on this station
/// that carry the BOM (our own writers never wrote one, so those are LabVIEW's saves) and off
/// NI's shipped Basic Interfaces example: every one BOM + CRLF. The writers in this repository
/// used <c>File.ReadAllText</c> /
/// <c>File.WriteAllText</c>, and that pair silently DROPS the BOM: the reader strips it and the
/// writer, on .NET, writes none. Two writers went further and joined a <c>.lvclass</c> with
/// <c>"\n"</c> after splitting it on <c>"\r\n"</c>-ending lines, and <c>LvClass.Project</c> built
/// a CRLF project with no BOM. A comment in <c>LvClass.AddToProject</c> claimed its line insert
/// "preserves ... the BOM", which it did not. LabVIEW loads either form, so nothing failed - every
/// edit simply turned into a whole-file diff against the next LabVIEW save, which is the noise
/// <c>lvproj-structure.md</c> section 8 exists to avoid.
///
/// THE RULES.
/// <list type="bullet">
/// <item>The BOM is kept when the file had one and not invented when it had none.</item>
/// <item>A file whose line breaks are all one kind is written back with that kind: every
/// <c>\r\n</c> and every bare <c>\n</c> in the text becomes the file's newline. A LONE <c>\r</c>
/// is never touched - LabVIEW's own save writes <c>\r\r\n</c> inside
/// <c>NI.LVClass.Geneology</c>, and normalising it is out of scope here; under a CRLF style that
/// sequence round-trips unchanged.</item>
/// <item>A MIXED file - CRLF and bare LF both present - is written back AS GIVEN. The text a
/// line-based edit leaves untouched keeps its own endings, and new lines take the MAJORITY ending
/// because the caller joins them with <see cref="Style.Newline"/>. Normalising a mixed file would
/// rewrite lines nobody edited.</item>
/// <item>A NEW file gets LabVIEW's style, BOM and CRLF (<see cref="Style.LabView"/>).</item>
/// </list>
/// </summary>
internal static class LvTextFile
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A file's byte style.</summary>
    /// <param name="HasBom">The file starts with the UTF-8 BOM.</param>
    /// <param name="Newline">The majority line ending, <c>"\r\n"</c> or <c>"\n"</c>.</param>
    /// <param name="Mixed">Both CRLF and bare LF occur - written back as given.</param>
    internal sealed record Style(bool HasBom, string Newline, bool Mixed)
    {
        /// <summary>What LabVIEW writes, and so what a new file gets.</summary>
        public static readonly Style LabView = new(true, "\r\n", false);
    }

    /// <summary>The text without its BOM, and the file's style.</summary>
    public static (string Text, Style Style) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = StartsWithBom(bytes);
        var text = Utf8NoBom.GetString(bytes, hasBom ? Bom.Length : 0,
                                       bytes.Length - (hasBom ? Bom.Length : 0));
        return (text, Detect(text, hasBom));
    }

    /// <summary>The text of a file, without its BOM.</summary>
    public static string ReadText(string path) => Read(path).Text;

    /// <summary>
    /// The style of a text. Only CRLF and bare LF are counted - a lone CR is not a line break here.
    /// A text with no line break at all gets LabVIEW's CRLF.
    /// </summary>
    public static Style Detect(string text, bool hasBom)
    {
        int crlf = 0, bareLf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++;
            else bareLf++;
        }
        var newline = bareLf > crlf ? "\n" : "\r\n";
        return new Style(hasBom, newline, crlf > 0 && bareLf > 0);
    }

    /// <summary>
    /// The text as it will be written under <paramref name="style"/>: every line break turned into
    /// the style's newline, or - for a mixed file - left exactly as given. A leading U+FEFF is
    /// dropped, so a caller that still holds one cannot write the BOM twice.
    /// </summary>
    public static string Normalise(string text, Style style)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        if (style.Mixed) return text;
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return style.Newline == "\n" ? lf : lf.Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    /// <summary>The bytes <see cref="Write(string,string,Style)"/> puts on disk.</summary>
    public static byte[] Encode(string text, Style style)
    {
        var body = Utf8NoBom.GetBytes(Normalise(text, style));
        return style.HasBom ? [.. Bom, .. body] : body;
    }

    /// <summary>Write <paramref name="text"/> in the style the file was READ with.</summary>
    public static void Write(string path, string text, Style style) =>
        File.WriteAllBytes(path, Encode(text, style));

    /// <summary>Asynchronous <see cref="Write(string,string,Style)"/>.</summary>
    public static Task WriteAsync(string path, string text, Style style, CancellationToken ct = default) =>
        File.WriteAllBytesAsync(path, Encode(text, style), ct);

    /// <summary>
    /// Write <paramref name="text"/> keeping whatever style the file on disk has now, or LabVIEW's
    /// style when there is no file yet. For a caller that did not read the file through
    /// <see cref="Read"/> itself.
    /// </summary>
    public static void Write(string path, string text) =>
        Write(path, text, File.Exists(path) ? Read(path).Style : Style.LabView);

    /// <summary>Asynchronous <see cref="Write(string,string)"/>.</summary>
    public static Task WriteAsync(string path, string text, CancellationToken ct = default) =>
        WriteAsync(path, text, File.Exists(path) ? Read(path).Style : Style.LabView, ct);

    /// <summary>A NEW file, in LabVIEW's style: BOM and CRLF.</summary>
    public static void WriteNew(string path, string text) => Write(path, text, Style.LabView);

    private static bool StartsWithBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == Bom[0] && bytes[1] == Bom[1] && bytes[2] == Bom[2];
}
