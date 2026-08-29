// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using WinDav.Abstractions;
using WinDav.Core;
using WinDav.Core.Logging;

namespace WinDav.Cli;

/// <summary>
/// The program, and the one place where a failure becomes a sentence and an exit code.
/// </summary>
internal static class Program
{
    /// <summary>What was asked for was done.</summary>
    internal const int Success = 0;

    /// <summary>The command was understood and did not succeed.</summary>
    internal const int Failed = 1;

    /// <summary>The command line was wrong, and nothing was attempted.</summary>
    internal const int Misused = 2;

    internal static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        using CancellationTokenSource cancellation = new();

        Console.CancelKeyPress += (_, pressed) =>
        {
            // The first Ctrl+C ends the mount in an orderly way. Killing the process would
            // leave the drive to WinFsp to clean up, which it does, but not gracefully.
            pressed.Cancel = true;

            cancellation.Cancel();
        };

        CommandLine line;
        LogSwitches switches;

        // Before anything is opened, because a recording asked for in a way that cannot be
        // read is a command line to correct, and a command line to correct leaves no file.
        try
        {
            line = CommandLine.Parse(args);
            switches = LogSwitches.Read(line);
        }
        catch (UsageException usage)
        {
            return WriteUsageProblem(usage.Message);
        }

        // The file is not created until something is written to it, so a command that has
        // nothing to say leaves nothing behind. The command line goes into the header, with
        // anything in it that could be a credential taken out first.
        using LogFile file = LogFile.Default(LogRedaction.CommandLine(args));

        // Started here and disposed before the file is, so that the line saying how the
        // recording ended is in the file it belongs to. A recording that ran its time out
        // has closed itself long before this.
        using LogRecording? recording = switches.Start(file);
        using FileLoggerFactory logging = new(file, recording, switches.Minimum);

        ILogger log = logging.CreateLogger(typeof(Program));

        try
        {
            int status = await RunAsync(line, logging, cancellation.Token).ConfigureAwait(false);

            // The command line itself is in the header of the file. What is worth a record of
            // its own is what came of it, because a command that answered nothing and one that
            // answered that it failed look the same from outside.
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug("{Command} answered {Status}.", line.Verb ?? "help", status);
            }

            return status;
        }
        catch (UsageException usage)
        {
            return WriteUsageProblem(usage.Message);
        }
        catch (InvalidDataException unknown)
        {
            // The registry answers a name it does not have with the names it does, which is
            // the sentence a misspelt provider needs.
            return WriteMisuse(unknown.Message);
        }
        catch (ArgumentException incomplete)
        {
            // A provider that was given settings it cannot work with, such as a Nextcloud
            // account without a login name.
            return WriteMisuse(incomplete.Message);
        }
        catch (ProviderException failure)
        {
            // A credential the server did not accept and a server that answered badly both
            // arrive here, and decision 74 has both always written down.
            return WriteFailure(log, failure, Describe(failure));
        }
        catch (InvalidOperationException unopenable)
        {
            // A credential that is where it belongs and cannot be opened, which is what one
            // written by another user or carried over from another machine looks like.
            return WriteFailure(log, unopenable, unopenable.Message);
        }
        catch (TimeoutException expired)
        {
            // A login that was begun in the browser and not granted. The token the server
            // handed out is gone with it, and there is nothing to carry on from.
            return WriteFailure(log, expired, expired.Message);
        }
        catch (Win32Exception refused)
        {
            // Windows turned the mount down, and says why better than we could.
            return WriteFailure(log, refused, refused.Message);
        }
        catch (TypeInitializationException driver)
        {
            return WriteDriverProblem(log, driver.InnerException ?? driver);
        }
        catch (DllNotFoundException driver)
        {
            return WriteDriverProblem(log, driver);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C before the mount was up. Nothing was left behind.
            return Success;
        }
    }

    private static async Task<int> RunAsync(
        CommandLine line,
        ILoggerFactory logging,
        CancellationToken cancellationToken)
    {
        if (line.Flag("--version"))
        {
            Console.WriteLine($"{ProductInfo.Name} {ProductInfo.Version}");

            return Success;
        }

        if (line.Verb is null || string.Equals(line.Verb, "help", StringComparison.Ordinal) || line.Flag("--help"))
        {
            WriteHelp();

            return Success;
        }

        if (string.Equals(line.Verb, "account", StringComparison.Ordinal))
        {
            return await AccountCommand.RunAsync(line, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(line.Verb, "mount", StringComparison.Ordinal))
        {
            return await MountCommand.RunAsync(line, logging, cancellationToken).ConfigureAwait(false);
        }

        throw new UsageException($"There is no command named '{line.Verb}'.");
    }

    private static void WriteHelp()
    {
        Console.WriteLine(
            $"""
            {ProductInfo.Name} {ProductInfo.Version}

            Usage:
              {ProductInfo.Slug} account add <url> [options]
              {ProductInfo.Slug} account list
              {ProductInfo.Slug} account remove <account>
              {ProductInfo.Slug} mount <mount>
              {ProductInfo.Slug} mount add <mount> --account <account> [options]
              {ProductInfo.Slug} mount list
              {ProductInfo.Slug} mount remove <mount>
              {ProductInfo.Slug} mount --account <account> [options]
              {ProductInfo.Slug} mount <url> [options]
              {ProductInfo.Slug} help
              {ProductInfo.Slug} --version

            <account> is the id or the uuid of an account, <mount> the name of a mount.

            Options of account add:
              --provider <name>    The kind of store: nextcloud (the default) or webdav.
              --user <name>        The login name, to be asked for an app password instead of
                                   logging in through the browser. Needed for webdav.
              --anonymous          Reach the store without a credential, instead of --user.
              --id <name>          What the account is called here. Default: <login>@<server>.

            Options of mount and mount add:
              --account <account>  The account to mount, instead of an address and a login.
              --path <path>        What becomes the root of the drive. Default: the whole account.
              --mount <X:|folder>  A drive letter, or an empty folder. Default: the next free letter.
              --label <text>       What the drive is called. Default: <user>@<server>, or the folder.
              --icon <file>        The drive icon, from an .ico. Default: the one for a network drive.
              --prefix <name>      The network name, as \\server\share. Default: \\<server>\<user>.
              --local              Appear as a local disk instead of as a network drive.

            Options of a mount made from an address, which mount add does not take:
              --provider <name>    The kind of store: nextcloud (the default) or webdav.
              --user <name>        The login name. Give it an app password, not the one to the account.
              --anonymous          Reach the store without a credential, instead of --user.

            Options of any command:
              --log <level>        What is written whatever happens: error, warn, info (the
                                   default), debug, trace, or off for nothing at all.
              --debug [areas]      Also write what was done, for a while.
              --trace [areas]      Also write every step of it, which is a great deal more.
              --for <time>         How long that lasts: 90s, 5m, 1h. Default: 60s, at most 1h.

            What was done and what failed is written to %LOCALAPPDATA%\{ProductInfo.Slug}\logs
            whether anything was asked for or not, and --log off stops that: nothing is
            written and no file is made. The areas of --debug and --trace are fs, http,
            provider and cli, separated by commas, and all of them when none is named.
            They belong after the command, as in "mount name --trace fs,http", because an
            option takes the word after it as its value. A recording ends by itself, when the
            time is up or when it has written 16 MB, and the file says which of the two it
            was; nothing starts it again. What --log asks for has no such end: it lasts as
            long as the command does.
            All four are read from the environment as well, as {LogSwitches.Variable(LogSwitches.LevelOption)}, {LogSwitches.Variable(LogSwitches.DebugOption)},
            {LogSwitches.Variable(LogSwitches.TraceOption)} and {LogSwitches.Variable(LogSwitches.ForOption)}; an option wins over its variable.

            The password is asked for, so that it stays out of the history of the shell.
            An account is written to the configuration; its credential is kept apart from it,
            encrypted for this user on this machine. Removing an account withdraws the password
            its login was given, unless another account here is signed in with the same one; a
            password that was typed in is withdrawn only if you say so.
            A server that lets one user in under more than one name is reached under the name
            the password was made for. Adding a second name for a user who is here already is
            asked about, and what comes of it is a second account for the same files.
            An account is named by its id or by its uuid, and account list shows both. The uuid
            is what a mount in the configuration points at, so a rename leaves the mount alone.
            A mount made from an account asks for nothing, because the server, the user and the
            credential are what the account holds; --provider, --user and --anonymous belong to
            a mount that names no account.
            A mount that is worth having again is written down with mount add, which asks
            nothing of a server, and is run afterwards by its name alone: what it was given is
            what it keeps, so a stored mount takes no options. mount list shows what is there,
            and mount remove takes one away without touching the account it was made from.
            A mount lasts as long as the command runs, and Ctrl+C takes it away.
            Everything on it is read only in this version.

            Exit codes: {Success} done, {Failed} failed, {Misused} the command line was wrong.
            """);
    }

    // Everything that goes to the error stream goes through one of these. They are ordinary
    // methods rather than lines in the catch blocks because Console.Error is a TextWriter,
    // and writing to one from inside an async method is a report of its own (CA1849) — one
    // that says nothing here, where a single line is written and the program then ends.
    private static int WriteUsageProblem(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine($"Try '{ProductInfo.Slug} help'.");

        return Misused;
    }

    private static int WriteMisuse(string message)
    {
        Console.Error.WriteLine(message);

        return Misused;
    }

    // A misuse is not written down: a command line with a typo in it should leave nothing
    // behind. A failure is, and this is the one place every one of them comes through.
    private static int WriteFailure(ILogger log, Exception failure, string message)
    {
        log.LogError(failure, "{Failure}", message);

        Console.Error.WriteLine(message);

        return Failed;
    }

    private static int WriteDriverProblem(ILogger log, Exception driver)
    {
        log.LogError(driver, "The WinFsp driver could not be loaded.");

        Console.Error.WriteLine(
            $"The WinFsp driver could not be loaded. {ProductInfo.Name} needs it installed; see https://winfsp.dev/.");
        Console.Error.WriteLine(driver.Message);

        return Failed;
    }

    private static string Describe(ProviderException failure)
    {
        string reason = failure.Error switch
        {
            ProviderError.PermissionDenied => "The server did not accept the credential.",
            ProviderError.NotFound => "There is nothing at that path on the server.",
            ProviderError.Unreachable => "The server could not be reached.",
            ProviderError.Protocol => "The server answered in a way that could not be made sense of.",
            _ => "The store could not be read.",
        };

        return $"{reason} {failure.Message}";
    }
}
