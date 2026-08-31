// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinDav.Core.Logging;

/// <summary>
/// What a record looks like in a file.
/// </summary>
/// <remarks>
/// <para>
/// One line per record: the time, the level, the area, and the sentence. The three fields in
/// front are padded so that the sentences line up under each other, and a line that carries
/// more than one line of text continues on the next, indented. That is the whole grammar,
/// and it is enough for a person reading and for a script counting.
/// </para>
/// <para>
/// Of everything
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#74-logging-five-levels-four-areas-and-a-switch-that-turns-itself-off">decision 74</see>
/// settles, this is the part that is expensive to take back, so it is deliberately dull:
/// no quoting, no escaping, nothing that has to be understood before the first line can
/// be read.
/// </para>
/// </remarks>
public static class LogFormat
{
    /// <summary>
    /// What a line that is not a record begins with: the header of a file and its last line.
    /// </summary>
    public const string CommentPrefix = "# ";

    /// <summary>
    /// What a line that carries on from the one above it begins with.
    /// </summary>
    public const string ContinuationPrefix = "  ";

    /// <summary>
    /// The end of a line. LF, as
    /// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#11-lf-in-the-working-tree-on-every-platform">decision 11</see>
    /// has it for everything else this repository writes.
    /// </summary>
    public const string LineEnd = "\n";

    // The same character, for the calls that append it on its own. A string of one
    // character is what CA1834 asks not to append, and no constant expression builds
    // the one from the other, so both stand here.
    private const char LineEndCharacter = '\n';

    // Sortable, unambiguous, and with the offset, so that a record can be held against a
    // server log written in another time zone without arithmetic in the reader's head.
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    // Wide enough for the longest name of each, so the sentences begin in one column.
    private const int LevelWidth = 5;
    private const int AreaWidth = 8;

    private const string FieldSeparator = "  ";

    // Whole where it is whole, and no more than three places where it is not.
    private const string NumberFormat = "0.###";

    private const long MegabyteFactor = 1024 * 1024;

    /// <summary>
    /// Builds the lines a file opens with.
    /// </summary>
    /// <param name="when">When the file was opened.</param>
    /// <param name="command">What was run, with anything sensitive already taken out.</param>
    /// <returns>Three comment lines, each ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is null.</exception>
    public static string Header(DateTimeOffset when, string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        StringBuilder text = new();

        text.Append(CommentPrefix)
            .Append(ProductInfo.Name)
            .Append(' ')
            .Append(ProductInfo.Version)
            .Append(", process ")
            .Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
            .Append(", started ")
            .Append(Timestamp(when))
            .Append(LineEndCharacter);

        text.Append(CommentPrefix).Append(command).Append(LineEndCharacter);
        text.Append(CommentPrefix).Append("time  level  area  message").Append(LineEndCharacter);

        return text.ToString();
    }

    /// <summary>
    /// Builds the line a file is closed with, which is how a reader tells a session that
    /// ended from one that was killed.
    /// </summary>
    /// <param name="when">When the file was closed.</param>
    /// <param name="records">How many records it holds.</param>
    /// <returns>One comment line, ended.</returns>
    public static string Footer(DateTimeOffset when, long records) =>
        $"{CommentPrefix}ended {Timestamp(when)} after {records.ToString(CultureInfo.InvariantCulture)} {(records == 1 ? "record" : "records")}{LineEnd}";

    /// <summary>
    /// Builds a line that says something about the file rather than about the product.
    /// </summary>
    /// <param name="when">When it is being said.</param>
    /// <param name="text">What is being said.</param>
    /// <returns>One comment line, ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <remarks>
    /// A comment carries the time for the same reason a record does: what a recording covers
    /// is read off the two lines that open and close it.
    /// </remarks>
    public static string Comment(DateTimeOffset when, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return $"{CommentPrefix}{Timestamp(when)}{FieldSeparator}{text}{LineEnd}";
    }

    /// <summary>
    /// Builds the text of the line a recording opens with.
    /// </summary>
    /// <param name="level">The level being recorded.</param>
    /// <param name="areas">The areas it covers.</param>
    /// <param name="duration">How long it may run.</param>
    /// <param name="bytes">How much it may write.</param>
    /// <returns>The text of a comment, without the prefix and without the end of the line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="areas"/> is null.</exception>
    /// <remarks>
    /// Both limits are written down where the recording is, so that a file which stops in the
    /// middle of something answers by itself why it did.
    /// </remarks>
    public static string RecordingStart(
        LogLevel level,
        IEnumerable<LogArea> areas,
        TimeSpan duration,
        long bytes)
    {
        ArgumentNullException.ThrowIfNull(areas);

        string where = string.Join(", ", areas.Select(LogAreas.Name));

        return $"recording {Name(level)} of {where} for {Seconds(duration)} or up to {Megabytes(bytes)}";
    }

    /// <summary>
    /// Builds the text of the line a recording closes with.
    /// </summary>
    /// <param name="end">Why it stopped.</param>
    /// <param name="records">How many records it wrote.</param>
    /// <returns>The text of a comment, without the prefix and without the end of the line.</returns>
    public static string RecordingEnd(LogRecordingEnd end, long records) =>
        $"recording ended after {records.ToString(CultureInfo.InvariantCulture)} {(records == 1 ? "record" : "records")}, {Reason(end)}";

    /// <summary>
    /// Builds one record.
    /// </summary>
    /// <param name="when">When it happened.</param>
    /// <param name="level">How loud it is.</param>
    /// <param name="area">Where it came from.</param>
    /// <param name="message">What it says.</param>
    /// <param name="exception">What went wrong, or <see langword="null"/>.</param>
    /// <returns>One line, ended, followed by an indented line for every further line of text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static string Line(
        DateTimeOffset when,
        LogLevel level,
        LogArea area,
        string message,
        Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(message);

        StringBuilder text = new();

        text.Append(Timestamp(when))
            .Append(FieldSeparator)
            .Append(Name(level).PadRight(LevelWidth))
            .Append(FieldSeparator)
            .Append(LogAreas.Name(area).PadRight(AreaWidth))
            .Append(FieldSeparator);

        AppendText(text, message);

        if (exception is not null)
        {
            // The stack as the runtime writes it, indented like any other continuation. It
            // is what decision 24 keeps the inner exception around for.
            AppendContinuation(text, exception.ToString());
        }

        return text.ToString();
    }

    /// <summary>
    /// Gets the name a level is written under.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <returns>One of the five names decision 74 settles on.</returns>
    public static string Name(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Warning => "warn",

        // Critical is not a sixth name. What it says is that something failed, and a reader
        // looking for what failed should not have to know two words for it.
        LogLevel.Error or LogLevel.Critical => "error",

        // Information, and whatever a later version of the enum adds. LogLevel.None asks for
        // nothing to be written and is turned away before a record is ever made.
        _ => "info",
    };

    private static string Timestamp(DateTimeOffset when) =>
        when.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    // Seconds throughout, whatever the person typed. Two minutes read as 120 s is one
    // subtraction away from the timestamps around it; read as 2 m it is two.
    private static string Seconds(TimeSpan duration) =>
        duration.TotalSeconds.ToString(NumberFormat, CultureInfo.InvariantCulture) + " s";

    private static string Megabytes(long bytes) =>
        (bytes / (double)MegabyteFactor).ToString(NumberFormat, CultureInfo.InvariantCulture) + " MB";

    private static string Reason(LogRecordingEnd end) => end switch
    {
        LogRecordingEnd.Duration => "the time was up",
        LogRecordingEnd.Size => "it had written as much as it may",

        // None does not reach here. A recording is closed with the reason it was closed for,
        // and the last of the three is the program ending underneath it.
        _ => "the session ended",
    };

    // The first line where it stands, every further line indented under it. A message with a
    // newline in it is rare and always worth keeping whole.
    private static void AppendText(StringBuilder text, string value)
    {
        string[] lines = Split(value);

        text.Append(lines[0]).Append(LineEndCharacter);

        for (int index = 1; index < lines.Length; index++)
        {
            text.Append(ContinuationPrefix).Append(lines[index]).Append(LineEndCharacter);
        }
    }

    private static void AppendContinuation(StringBuilder text, string value)
    {
        foreach (string line in Split(value))
        {
            text.Append(ContinuationPrefix).Append(line).Append(LineEndCharacter);
        }
    }

    private static string[] Split(string value) =>
        value.ReplaceLineEndings(LineEnd).Split(LineEnd);
}
