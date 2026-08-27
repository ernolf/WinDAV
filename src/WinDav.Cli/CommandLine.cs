// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Cli;

/// <summary>
/// What was typed, taken apart.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand, because the surface is one verb wide. A parser library is a dependency
/// with a say in how the commands look, and that say is worth having only once there are
/// enough commands for it to matter.
/// </para>
/// <para>
/// An option is <c>--name value</c> or <c>--name=value</c>. An option followed by another
/// option has no value, which is how a flag is told from the rest: it is not a list kept
/// somewhere, it is what was written.
/// </para>
/// </remarks>
internal sealed class CommandLine
{
    private const string OptionMark = "--";

    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);

    private readonly List<string> _arguments = [];

    private CommandLine()
    {
    }

    /// <summary>
    /// Gets the command that was asked for, or <see langword="null"/> when none was.
    /// </summary>
    internal string? Verb { get; private set; }

    /// <summary>
    /// Gets everything written after the verb that is not an option, in the order it was
    /// written.
    /// </summary>
    /// <remarks>
    /// A verb with parts to it, as in <c>account add</c>, reads the part from here. What a
    /// command with one argument wants is <see cref="SingleArgument"/>.
    /// </remarks>
    internal IReadOnlyList<string> Arguments => _arguments;

    /// <summary>
    /// Takes a command line apart.
    /// </summary>
    /// <param name="tokens">What the shell handed over.</param>
    /// <returns>The parts of it.</returns>
    /// <exception cref="UsageException">
    /// An option has no name, or the same option was given twice. Which of two values would
    /// win is not a question to answer by the order they were typed in.
    /// </exception>
    internal static CommandLine Parse(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        CommandLine line = new();

        for (int index = 0; index < tokens.Count; index++)
        {
            string token = tokens[index];

            if (!token.StartsWith(OptionMark, StringComparison.Ordinal))
            {
                if (line.Verb is null)
                {
                    line.Verb = token;
                }
                else
                {
                    line._arguments.Add(token);
                }

                continue;
            }

            string name = token;
            string? value = null;
            int assignment = token.IndexOf('=', StringComparison.Ordinal);

            if (assignment >= 0)
            {
                name = token[..assignment];
                value = token[(assignment + 1)..];
            }
            else if (index + 1 < tokens.Count
                && !tokens[index + 1].StartsWith(OptionMark, StringComparison.Ordinal))
            {
                value = tokens[index + 1];
                index++;
            }

            if (name.Length <= OptionMark.Length)
            {
                throw new UsageException("An option needs a name after the two dashes.");
            }

            if (!line._options.TryAdd(name, value))
            {
                throw new UsageException($"The option {name} was given more than once.");
            }
        }

        return line;
    }

    /// <summary>
    /// Reads an option that stands for itself.
    /// </summary>
    /// <param name="name">The option, dashes included.</param>
    /// <returns>Whether it was given.</returns>
    /// <exception cref="UsageException">It was given a value, which it has no use for.</exception>
    internal bool Flag(string name)
    {
        if (!_options.TryGetValue(name, out string? value))
        {
            return false;
        }

        if (value is not null)
        {
            // Named, because the likeliest way to get here is a flag written in front of
            // something that belongs elsewhere, as in "mount --anonymous https://server".
            throw new UsageException($"The option {name} takes no value, and '{value}' was read as one.");
        }

        return true;
    }

    /// <summary>
    /// Reads an option that carries a value.
    /// </summary>
    /// <param name="name">The option, dashes included.</param>
    /// <returns>The value, or <see langword="null"/> when the option was not given.</returns>
    /// <exception cref="UsageException">It was given without a value.</exception>
    internal string? Value(string name)
    {
        if (!_options.TryGetValue(name, out string? value))
        {
            return null;
        }

        return value ?? throw new UsageException($"The option {name} needs a value.");
    }

    /// <summary>
    /// Reads the one thing a command was asked to act on.
    /// </summary>
    /// <param name="what">What that thing is, for the message when it is missing.</param>
    /// <returns>The argument.</returns>
    /// <exception cref="UsageException">There is not exactly one.</exception>
    internal string SingleArgument(string what)
    {
        return _arguments.Count switch
        {
            1 => _arguments[0],
            0 => throw new UsageException($"This command needs {what}."),
            _ => throw new UsageException($"This command takes {what} and nothing else."),
        };
    }

    /// <summary>
    /// Refuses an option the command has no use for.
    /// </summary>
    /// <param name="known">The options the command reads, dashes included.</param>
    /// <exception cref="UsageException">
    /// Something else was given. An option that is silently ignored is worse than one that is
    /// refused: it looks as though it had an effect.
    /// </exception>
    internal void EnsureOnlyKnown(IReadOnlyCollection<string> known)
    {
        ArgumentNullException.ThrowIfNull(known);

        foreach (string name in _options.Keys)
        {
            if (!known.Contains(name))
            {
                throw new UsageException($"This command has no option {name}.");
            }
        }
    }
}
