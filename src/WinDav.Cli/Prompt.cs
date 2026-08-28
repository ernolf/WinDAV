// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace WinDav.Cli;

/// <summary>
/// Asks the person at the keyboard for what must not be written on a command line.
/// </summary>
/// <remarks>
/// A password given as an option is a password in the history of the shell and in the list
/// of running processes, which is why no command has an option for one. Input that comes
/// from a pipe is read as a line: a script that feeds a password in has already decided
/// where it keeps it.
/// </remarks>
internal static class Prompt
{
    /// <summary>
    /// Asks for a credential without showing it.
    /// </summary>
    /// <param name="question">What to write before the cursor, without a newline.</param>
    /// <returns>What was typed.</returns>
    /// <exception cref="UsageException">Nothing was typed.</exception>
    internal static string ReadSecret(string question)
    {
        Console.Write(question);

        string secret = Console.IsInputRedirected
            ? Console.ReadLine() ?? string.Empty
            : ReadHidden();

        if (secret.Length == 0)
        {
            throw new UsageException("No password was given.");
        }

        return secret;
    }

    /// <summary>
    /// Asks something that is done only if it is said to be.
    /// </summary>
    /// <param name="question">What to write before the cursor, without a newline.</param>
    /// <returns><see langword="true"/> when the answer begins with a y.</returns>
    /// <remarks>
    /// Anything else is a no, the end of the input among it: a command whose input comes from
    /// a file has nobody at the keyboard, and what is not answered is not done.
    /// </remarks>
    internal static bool Confirm(string question)
    {
        Console.Write(question);

        string? answer = Console.ReadLine();

        return answer is not null && answer.TrimStart().StartsWith('y');
    }

    private static string ReadHidden()
    {
        StringBuilder typed = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();

                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }

                continue;
            }

            // A key that stands for no character, an arrow key among them, arrives as a
            // control character and is not part of what was typed.
            if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }
}
