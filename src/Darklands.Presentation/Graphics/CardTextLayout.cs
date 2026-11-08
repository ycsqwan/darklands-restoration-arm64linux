using Darklands.Cards;

namespace Darklands.Presentation.Graphics;

/// <summary>What choosing an option line does: a plain choice, or a pop-up of items or saints.</summary>
// orig: hs->type[] written by text-paint-wrapped root 0265:0134 draft (8 of 10 functions of the file match, this one does not): marker 0x15 -> 0, 0x10 -> 1, 0x16 -> 2, 0x06 -> -1; tests: none (drawing; needs a font and a screen)
public enum OptionKind
{
    Battle = -1,
    Plain = 0,
    Items = 1,
    Saints = 2,
}

/// <summary>The screen area of a choosable option line (all option areas share x and width).</summary>
// orig: struct hotspots (DS:E96E, 0xf6 bytes): count, width, x, y[20], height[20], enabled[20], index[20], type[20] — one entry per ENABLED option line (`enabled == 1`); a line shown but not choosable (2) records nothing and a hidden one (0) is skipped
// C: an option line's height is the sum of its wrapped lines: each line adds `spacing`, the last (at the newline or end of text) adds `leading + spacing`.
public sealed record OptionArea(int Option, OptionKind Kind, int Y, int Height);

/// <summary>
/// Lays out and draws card text the way the game does: word wrapping at spaces and hyphens,
/// control codes for paragraphs (0x14), hanging indents (0x1D), conditional passages
/// (0x01 … 0x13), option lines (0x15 plain, 0x10 items, 0x16 saints, 0x06 battle) and an
/// illuminated first capital.
/// </summary>
// orig: text-paint-wrapped root 0265:0134 draft (the C is read directly; it does not match the bytes: loop shapes and stack slots only); wrapped-text-size root 0265:0654 (the same layout without drawing) is its own second copy of the loop, with its own differences: MeasureHeight; tests: TextLayoutCallsTests (direct calls of both)
// C: written from the loop line by line, including its quirks: the 0x01 and 0x13 branches advance the cursor twice (once in the branch, once at the loop end), so the byte after a conditional marker is skipped and counted; an empty segment widens the column by one pixel for the rest of the text; the indent width is a char cast to unsigned char.
// DIVERGENCE: `stop` (StopAtFirstOption), `init` (the hotspot table reset) and `icon` (the drop cap) are the C's parameters; init is always on here. The original draws a character with the driver's glyph-width/gfx-text-draw (asm); the glyph widths come from the font file here (TextPainter.Advance), so a byte the font lacks counts as width 0.
public sealed class CardTextLayout(TextPainter painter)
{
    public const byte Paragraph = 0x14;
    public const byte Indent = 0x1D;
    public const byte ConditionStart = 0x01;
    public const byte ConditionEnd = 0x13;
    public const byte PlainOption = 0x15;
    public const byte ItemOption = 0x10;
    public const byte SaintOption = 0x16;
    public const byte BattleOption = 0x06;
    public const int DropCapSize = 21;

    public sealed class Settings
    {
        /// <summary>State of each option line, by option number (missing = enabled).</summary>
        public IReadOnlyList<OptionState> Options { get; init; } = [];

        /// <summary>Which conditional passages are shown, by order of appearance.</summary>
        public IReadOnlyList<bool> Conditions { get; init; } = [];

        /// <summary>Stop at the first option line (draw only the text before the options).</summary>
        public bool StopAtFirstOption { get; init; }

        /// <summary>Extra pixels between lines.</summary>
        public int Leading { get; init; } = 1;

        /// <summary>Height of a line before the leading (the game's "spacing"), or null for the font's height.</summary>
        public int? Spacing { get; init; }

        /// <summary>How each line sits in the column: left, centred or right-aligned.</summary>
        // orig: DS:01BC (DGROUP byte, 0 left, 1 centred, 2 right) switch before each gfx-text-draw in text-paint-wrapped; the side-window texts (R043+0x27E6, R043+0x29D0) and the party-panel status texts set it to 1 around the call
        public TextAlignment Alignment { get; init; } = TextAlignment.Left;

        /// <summary>Picture for an illuminated capital, or null to draw the first letter normally.</summary>
        public Func<int, Content.Formats.Graphics.IndexedImage?>? DropCap { get; init; }

        public byte Color { get; init; }

        /// <summary>Colour of the option lines (from the first option marker on), or null to keep <see cref="Color"/>.</summary>
        public byte? OptionColor { get; init; }

        /// <summary>How the option lines' pixels combine with the screen (the card's 0x380: colour 0x80 by xor).</summary>
        public TextMode OptionMode { get; init; } = TextMode.Copy;
    }

    /// <summary>Draws <paramref name="text"/> in the column (x, y, width) and returns the option areas.</summary>
    // orig: text-paint-wrapped(w, text, width, x, y, spacing, hs, init, stop, leading, icon, color) root 0265:0134
    public List<OptionArea> Draw(Canvas canvas, byte[] text, int x, int y, int width, Settings settings) =>
        Draw(canvas, TextDecoder.Decode(text), x, y, width, settings);

    /// <summary>Draws codepoints (see <see cref="TextDecoder"/>) in the column; a colour code arrives as a negative value.</summary>
    public List<OptionArea> Draw(Canvas canvas, int[] text, int x, int y, int width, Settings settings)
    {
        var areas = new List<OptionArea>();
        var spacing = settings.Spacing ?? painter.LineHeight;
        var lineX = x;
        var lineWidth = width;
        var resetAtLineEnd = true;
        var indentPending = false;
        var indentWidth = 0;
        var optionNumber = 0;
        var conditionNumber = 0;
        OptionArea? open = null;
        var start = 0;
        var cursor = 0;
        var color = settings.Color;
        var mode = TextMode.Copy;

        if (settings.DropCap is { } dropCap && text.Length > 0 && text[0] is > '@' and < '[' &&
            dropCap(text[0]) is { } cap)
        {
            // C: the letter is a saved driver picture (gfx-rect-save at start-up) drawn with gfx-picture-draw: colour 0 transparent, its black is colour 5.
            canvas.Draw(cap, x, y, transparent: true);
            lineX = x + DropCapSize;
            lineWidth = width - DropCapSize;
            y += DropCapSize - spacing;
            cursor = 1;
        }
        var lineStart = cursor;

        void CloseArea(int extra)
        {
            if (open is not null)
            {
                areas[^1] = open with { Height = open.Height + extra };
                open = null;
            }
        }

        void GrowArea(int extra)
        {
            if (open is not null)
            {
                open = open with { Height = open.Height + extra };
                areas[^1] = open;
            }
        }

        // BUG: the 0x01 / 0x13 branches advance the cursor twice, so a text that ends right after a conditional marker has its zero skipped and the loop
        //   runs on past the text's end (reachable in play: card $situa00 block 4 ends with 0x13); what is drawn then is undefined memory (D-0d3a38),
        //   so the rewrite registers it and stops at the text's end.
        // DIVERGENCE: a text that ends right after a conditional marker (0x13, or a 0x01 whose passage is shown) has its terminating zero skipped by the
        //   double advance, and the original reads on past the end into the leftovers of the shared buffer DS:9085 (whatever an earlier, longer string
        //   left there: lines and option lines of another text, drawn and given hot spots); here the text ends at its end. Found by the direct calls of
        //   text-paint-wrapped (tools/oracle/text_calls.py, batch 34: card $situa00 block 4 ends with 0x13). Also the original bug B-fe596a.
        int At(int i) => i >= 0 && i < text.Length ? text[i] : 0;

        var more = true;
        while (more)
        {
            var count = 0;
            var used = 0;
            while (used < lineWidth && At(cursor) != 0 && At(cursor) != '\n')
            {
                var c = At(cursor);
                if (!IsControl(c))
                {
                    used += painter.Advance(c);
                }
                else if (c == Paragraph)
                {
                    y += 2;
                }
                else if (c == ConditionStart)
                {
                    cursor++;
                    count++;
                    var shown = conditionNumber < settings.Conditions.Count && settings.Conditions[conditionNumber];
                    if (!shown)
                    {
                        while (At(cursor) != ConditionEnd && At(cursor) != 0)
                        {
                            cursor++;
                        }
                        lineStart = cursor + 1;
                        count = -1;
                    }
                    conditionNumber++;
                }
                else if (c == ConditionEnd)
                {
                    cursor++;
                    count++;
                }
                else if (IsOptionMarker(c))
                {
                    if (settings.StopAtFirstOption)
                    {
                        more = false;
                        break;
                    }
                    // orig: `if (color == 1) w->color = 0x380;` at an option marker: from here on colour 0x80, drawn by xor (TextMode)
                    color = settings.OptionColor ?? color;
                    mode = settings.OptionColor is null ? mode : settings.OptionMode;
                    var state = optionNumber < settings.Options.Count ? settings.Options[optionNumber] : OptionState.Enabled;
                    if (state == OptionState.Enabled)
                    {
                        open = new OptionArea(optionNumber, KindOf(c), y, 0);
                        areas.Add(open);
                    }
                    else if (state == OptionState.Hidden)
                    {
                        while (At(cursor) != '\n' && At(cursor) != 0)
                        {
                            cursor++;
                        }
                        if (At(cursor) == 0)
                        {
                            lineStart = cursor;
                            cursor--;
                        }
                        else
                        {
                            lineStart = cursor + 1;
                        }
                        count = -1;
                    }
                    optionNumber++;
                }
                else if (c == Indent)
                {
                    // orig: text-paint-wrapped root 0265:0134 at 0265:0431..045F: the count bytes from the line's start copied raw, then text-width-px
                    //   (root 0265:084E) of the copy, kept as a byte
                    indentWidth = painter.Measure(Raw(text, lineStart, count));
                    resetAtLineEnd = false;
                    indentPending = true;
                }
                count++;
                cursor++;
            }

            if (used >= lineWidth)
            {
                cursor--;
                count--;
            }
            var overflowed = used >= lineWidth;
            if (At(cursor) == '\n' || At(cursor) == 0)
            {
                resetAtLineEnd = true;
                CloseArea(settings.Leading + spacing);
            }

            // Break the line after the last space or hyphen that fits. A line with no break
            // opportunity at all keeps its width-limited break: Chinese has no spaces, so walking
            // back to the line's start would throw the whole line away.
            var breakCursor = cursor;
            // Walking back to a space is only needed when the column edge falls inside a word. Chinese breaks
            // between any two characters, so a line that ends in Chinese keeps its width-limited break: walking
            // back to the last space of such a line leaves it far short of the column (a line of one word).
            var cjkEdge = At(cursor) > 0xFF || (cursor > lineStart && At(cursor - 1) > 0xFF);
            while (!cjkEdge && At(breakCursor) is not ' ' and not 0 and not '\n' and not '-' &&
                   start < breakCursor && breakCursor > lineStart)
            {
                breakCursor--;
            }
            // The walk-back only counts when it actually found a break opportunity; with the cursor left on the
            // first character that did not fit, there is nothing to step over and the next line starts there.
            var broke = !cjkEdge && (breakCursor > lineStart || At(breakCursor) == ' ' || At(breakCursor) == '-');
            if (broke)
            {
                cursor = breakCursor;
            }
            count = cursor - lineStart;

            // Chinese sets no closing punctuation at the start of a line: the break moves back over it, so
            // the line ends one character sooner and the punctuation follows a character on the next one.
            while (!broke && overflowed && count > 1 && IsNoStart(At(cursor)))
            {
                cursor--;
                count--;
            }

            // Nothing but spaces left on this line: the line ends here.
            var rest = cursor;
            while (At(rest) == ' ')
            {
                rest++;
            }
            if (At(rest) == '\n' || At(rest) == 0)
            {
                resetAtLineEnd = true;
                CloseArea(settings.Leading + spacing);
                cursor = rest;
            }

            if (At(cursor) == '-')
            {
                count++;
            }
            if (At(cursor) == 0)
            {
                more = false;
            }

            if (count > 0)
            {
                // C: case 1 puts the line at (lwk - width) / 2 + lxk and case 2 at lwk - width + lxk, in int arithmetic (division truncates towards zero, so a line wider than the column moves left by half the excess)
                var line = Printable(text, lineStart, count);
                var lineLeft = settings.Alignment switch
                {
                    TextAlignment.Center => (lineWidth - painter.Measure(line)) / 2 + lineX,
                    TextAlignment.Right => lineWidth - painter.Measure(line) + lineX,
                    _ => lineX,
                };
                painter.Draw(canvas, lineLeft, y, line, color, mode);
                GrowArea(spacing);
                y += settings.Leading + spacing;
            }
            else
            {
                width++;   // the original widens the column by a pixel for every empty segment
            }

            // A line that broke at a space sits on that space and a line that ended at a newline sits on
            // the newline: both are stepped over. A line that broke at the column edge already sits on the
            // first character that did not fit, which is where the next line starts, so stepping over it
            // would lose that character (one per line of a text without spaces, i.e. every Chinese line).
            if (broke || !overflowed)
            {
                cursor++;
            }
            lineStart = cursor;
            if (indentPending)
            {
                lineX = x + (byte)indentWidth;
                lineWidth = width - (byte)indentWidth;
                indentPending = false;
            }
            if (resetAtLineEnd)
            {
                lineX = x;
                lineWidth = width;
            }
        }
        EndY = y;
        return areas;
    }

    /// <summary>Where the next line would start after the last <see cref="Draw"/>: the text's bottom edge.</summary>
    // orig: the y that wrapped-text-size root 0265:0654 returns in the high word (minus 1): the side-window hint centres itself on it (R043+0x27E6)
    public int EndY { get; private set; }

    /// <summary>
    /// The end y of the text laid out from y 0 in a column of <paramref name="width"/>, as wrapped-text-size counts it (its own copy of
    /// the layout loop, without drawing): the high word it returns plus one.
    /// </summary>
    /// <param name="text">The text buffer. Bytes past the array read as 0; a caller whose buffer holds bytes after the text's zero
    /// (the pop-up box, <see cref="OptionPopupText"/>) passes them too, since the loop can read on past the zero.</param>
    /// <param name="options">The option states the loop reads from its table (enabled[] at +0x2E); null for the callers that pass no
    /// table (their texts hold no option marker), read as every line shown.</param>
    // C: with no table (0) the original reads enabled[n] at DS:002E + n, the run-time library's copyright string (n = 0..18 non-zero, 19 zero;
    //   hooked at root 0265:07C8); the callers that pass 0 (the away member's "???? ???? ????", the side window's hints) measure texts without
    //   option markers, so it is never read in play. The test of the direct calls gives those bytes (TextLayoutCallsTests.NullTable).
    // orig: wrapped-text-size root 0265:0654 draft (8 of 10 functions of its file match, this one does not), read from the asm
    //   (0265:0654..084C) and written line by line; its four callers pass x 0 (unused), the window font's line height, stop 0, leading 0, icon 0
    //   (root 0000:1AFE / 0000:1C68, R043+0x2972, R043+0x2B05)
    // C: how it differs from text-paint-wrapped's loop (Draw): no conditional passages (0x01 and 0x13 are glyphs); the word break walks back
    //   to the text's start, not the line's; no "only spaces left" end; a hidden option line (enabled[n] == 0) is skipped to its newline OR
    //   its zero, and the cursor then steps past that zero: when the text ends inside a hidden option line, the loop reads on into the
    //   bytes after the text's zero (the pop-up box: the 0x14 its builder cut off, one more line); an empty segment widens the column.
    // C: checked by direct calls of the original (tools/oracle/text_calls.py, batch 34 jobs, 4,060 jobs): every height equal.
    // DIVERGENCE: a translation table (Chinese text) cannot use this loop: its word break walks back to the text's start, and a Chinese
    //   line has no space to stop at, so the line would be thrown away. With a table loaded the height is measured by the drawing
    //   layout's own break instead (Draw's cjkEdge).
    public int MeasureHeight(byte[] text, int width, int leading, IReadOnlyList<OptionState>? options = null) =>
        MeasureHeight(TextDecoder.Decode(text), width, leading, options);

    /// <summary>The height codepoints would take in a column of <paramref name="width"/>, without drawing anywhere visible.</summary>
    public int MeasureHeight(int[] text, int width, int leading, IReadOnlyList<OptionState>? options = null) =>
        MeasureHeight(text, width, new Settings { Leading = leading, Options = options ?? [] });

    /// <summary>The height of the text laid out with these settings (hidden option lines left out).</summary>
    public int MeasureHeight(byte[] text, int width, Settings settings) =>
        MeasureHeight(TextDecoder.Decode(text), width, settings);

    public int MeasureHeight(int[] text, int width, Settings settings)
    {
        // A translation table means Chinese text: the faithful loop's word break walks back to the text's start, and a
        // Chinese line has no space to stop at, so the line would be thrown away. The drawing layout's own break
        // (Draw's cjkEdge) measures it instead.
        if (TranslationTable.Current is not null)
        {
            var scratch = new Canvas(new Platform.Screen());
            Draw(scratch, text, 0, 0, width, settings);
            return EndY;
        }

        var options = settings.Options;
        var leading = settings.Leading;
        int At(int i) => i >= 0 && i < text.Length ? text[i] : 0;
        var spacing = painter.LineHeight;
        var y = 0;
        var indentPending = false;
        var reset = true;
        var more = true;
        var option = 0;
        var indentWidth = 0;
        var cursor = 0;
        var lineStart = 0;
        var lineWidth = width;
        while (more)
        {
            var count = 0;
            var used = 0;
            while (lineWidth > used)
            {
                var c = At(cursor);
                if (c is 0 or '\n')
                {
                    break;
                }
                if (c == Paragraph)
                {
                    y += 2;
                }
                else if (c is PlainOption or ItemOption or SaintOption or BattleOption)
                {
                    var state = options is not null && option < options.Count ? options[option] : OptionState.Enabled;
                    if (state == OptionState.Hidden)
                    {
                        while (At(cursor) != '\n' && At(cursor) != 0)
                        {
                            cursor++;
                        }
                        lineStart = cursor + 1;
                        count = -1;
                    }
                    option++;
                }
                else if (c == Indent)
                {
                    indentWidth = (byte)painter.Measure(Raw(text, lineStart, count));
                    reset = false;
                    indentPending = true;
                }
                else if (c != 0x0D)
                {
                    used += painter.Advance(c);
                }
                count++;
                cursor++;
            }

            if (At(cursor) == '\n')
            {
                reset = true;
            }
            if (lineWidth <= used)
            {
                cursor--;
                count--;
            }
            while (At(cursor) is not ' ' and not 0 and not '\n' and not '-' && cursor > 0)
            {
                count--;
                cursor--;
            }
            if (At(cursor) == '-')
            {
                count++;
            }
            if (At(cursor) == 0)
            {
                more = false;
            }
            if (count > 0)
            {
                y += leading + spacing;
            }
            else
            {
                width++;   // as Draw: an empty segment widens the column by a pixel
            }

            cursor++;
            lineStart = cursor;
            if (indentPending)
            {
                lineWidth = width - indentWidth;
                indentPending = false;
            }
            if (reset)
            {
                lineWidth = width;
            }
        }
        return y;
    }

    // C: the width of the part of the line before the 0x1D marker is text-width-px of a copy of the raw bytes, control codes included (their glyph-width is whatever the driver gives them, TextPainter.Advance: the font's own widths for the codes it holds). Checked against the original by direct calls (tools/oracle/text_calls.py, batch 34): with font 0 (slot 1), whose table starts at code 0, leaving the codes out moved the indented lines of 26 texts.
    // C: glyph-width (drv+0x11A9) gives a code outside the font's first..last range no width: fonts 1 and 2 start at 0x1F, so in the card
    //   window's font the control codes take no room; font 0 starts at code 0 and gives them the widths of its table. checked against asm
    private static bool IsOptionMarker(int c) => c is PlainOption or ItemOption or SaintOption or BattleOption;

    /// <summary>The characters a Chinese line may not begin with (closing punctuation and the like).</summary>
    private static bool IsNoStart(int c) =>
        c is 0x3001 or 0x3002 or 0xFF0C or 0xFF1B or 0xFF1A or 0xFF1F or 0xFF01 or 0xFF09 or 0x300D
            or 0x300F or 0x3011 or 0x300B or 0x3009 or 0x2026 or 0xFF5D or 0x3015 or 0x2019 or 0x201D;

    private static bool IsControl(int c) =>
        c is Paragraph or PlainOption or ItemOption or SaintOption or BattleOption or Indent or 0x0D
            or ConditionStart or ConditionEnd;

    private static OptionKind KindOf(int marker) => marker switch
    {
        ItemOption => OptionKind.Items,
        SaintOption => OptionKind.Saints,
        BattleOption => OptionKind.Battle,
        _ => OptionKind.Plain,
    };

    /// <summary>The codepoints text[from .. from+count) as they are, control codes included.</summary>
    private static int[] Raw(int[] text, int from, int count) =>
        from >= 0 && from < text.Length && count > 0 ? text.AsSpan(from, Math.Min(count, text.Length - from)).ToArray() : [];

    /// <summary>The visible characters of text[from .. from+count), control codes removed.</summary>
    private static int[] Printable(int[] text, int from, int count)
    {
        var result = new List<int>(Math.Max(0, count));
        for (var i = 0; i < count && from + i < text.Length; i++)
        {
            var c = text[from + i];
            if (!IsControl(c))
            {
                result.Add(c);
            }
        }
        return [.. result];
    }
}

/// <summary>Horizontal alignment of wrapped text lines (the original's DS:01BC).</summary>
public enum TextAlignment
{
    Left = 0,
    Center = 1,
    Right = 2,
}
