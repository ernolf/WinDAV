// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace WinDav.Cli;

/// <summary>
/// Writes rows in columns that line up.
/// </summary>
/// <remarks>
/// In one place because more than one command lists something, and a table that is laid out
/// twice is laid out differently as soon as one of the two is changed; decisions.md 73.
/// </remarks>
internal static class Table
{
    /// <summary>
    /// Writes a table, the first row being the headings.
    /// </summary>
    /// <param name="rows">The rows, each with the same number of columns.</param>
    internal static void Write(IReadOnlyList<string[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        int columns = rows[0].Length;
        int[] widths = new int[columns];

        foreach (string[] row in rows)
        {
            for (int column = 0; column < columns; column++)
            {
                widths[column] = Math.Max(widths[column], row[column].Length);
            }
        }

        foreach (string[] row in rows)
        {
            StringBuilder written = new();

            for (int column = 0; column < columns; column++)
            {
                // The last column is not padded: trailing spaces are what a line copied out
                // of a terminal carries with it.
                written.Append(column == columns - 1 ? row[column] : row[column].PadRight(widths[column] + 2));
            }

            Console.WriteLine(written.ToString());
        }
    }
}
