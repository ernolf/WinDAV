// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using WinDav.Abstractions;
using WinDav.Core;
using WinDav.Core.Logging;
using WinDav.Core.Providers;
using WinDav.Fs;

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
        ReadSettings reads;
        TimeSpan attributes;
        DirectorySettings directories;

        // Before anything is opened, because a recording asked for in a way that cannot be
        // read is a command line to correct, and a command line to correct leaves no file.
        try
        {
            line = CommandLine.Parse(args);
            switches = LogSwitches.Read(line);
            reads = ReadSwitches.Read(line);
            attributes = CacheSwitches.Read(line);
            directories = DirectorySwitches.Read(line);
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
            int status = await RunAsync(line, reads, attributes, directories, logging, cancellation.Token).ConfigureAwait(false);

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
        ReadSettings reads,
        TimeSpan attributes,
        DirectorySettings directories,
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
            return await MountCommand.RunAsync(line, reads, attributes, directories, logging, cancellationToken).ConfigureAwait(false);
        }

        throw new UsageException($"There is no command named '{line.Verb}'. There is account, mount and help.");
    }

    private static void WriteHelp()
    {
        Console.WriteLine(
            $"""
            {ProductInfo.Name} {ProductInfo.Version}

            Mount a WebDAV or Nextcloud store as a Windows drive. The drive is read only in
            this version, and it lasts as long as the command runs: Ctrl+C takes it away.

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

            To get started:
              {ProductInfo.Slug} account add https://cloud.example.com
              {ProductInfo.Slug} mount add work --account alice@cloud.example.com --mount W:
              {ProductInfo.Slug} mount work

            The first opens a browser to log in and writes the account down, under the name
            that '{ProductInfo.Slug} account list' then shows. The second writes down a mount called
            work on drive W:, and asks nothing of a server. The third brings the drive up and
            holds it there until you press Ctrl+C.

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
              --read-ahead <size>  How much a mount may fetch at a time: 8m, or off.
              --read-ahead-total <size>
                                   How much of that all open handles may hold: 64m, or off.
              --requests <count>   How many requests may be on the wire at once: 2, or off.
              --attributes <time>  How long what the server said about an entry is believed:
                                   10s, or off.
              --list-ahead <levels>
                                   How far below an open directory a mount lists ahead: 1, or off.
              --list-ahead-requests <count>
                                   How many requests one round of that may make: 32, or off.
              --listings <count>   How many directory listings are held at once: 512, or off.

            Logging:
              A record is written to %LOCALAPPDATA%\{ProductInfo.Slug}\logs whatever happens,
              and --log says how much of it. --log off writes nothing and makes no file at
              all. What --log asks for lasts as long as the command does.
              --debug and --trace write a great deal more, and only for a while. --for says
              how long; a recording also ends when it has written 16 MB, and the file says
              which of the two ended it. Nothing starts it again.
              Both take areas: fs, http, provider and cli, separated by commas, and all of
              them when none is named. Write options after the command, because an option
              takes the word after it as its value:
                {ProductInfo.Slug} mount work --trace fs,http --for 2m
              All four can be set in the environment instead, as {Switches.Variable(LogSwitches.LevelOption)}, {Switches.Variable(LogSwitches.DebugOption)},
              {Switches.Variable(LogSwitches.TraceOption)} and {Switches.Variable(LogSwitches.ForOption)}. An option wins over its variable.

            Reading:
              A request costs the server about the same quarter second whether it asks for a
              kilobyte or for eight megabytes. --read-ahead is how much a mount fetches at a
              time where a read carries on from where the last one ended, and it keeps that
              much for the handle that asked. A read that lands anywhere else, or one larger
              than that, is a request of its own.
              --read-ahead-total is the ceiling over all open handles together. A handle that
              finds it used up reads without a window rather than waiting for one.
              --requests is how many requests may be on the wire at the same time. A mount
              lowers it itself while the server answers that it is busy, and raises it again
              slowly.
              Sizes are bytes, or a number with k, m or g after it. Each of the three takes
              off, and with all three off every read is one request for exactly what was read,
              with nothing kept between them.
              These three can be set in the environment instead, as {Switches.Variable(ReadSwitches.WindowOption)},
              {Switches.Variable(ReadSwitches.TotalOption)} and {Switches.Variable(ReadSwitches.RequestsOption)}.

            Attributes:
              Opening a file asks the server about it twice, and a listing is told about every
              entry of a directory for what one of them costs. --attributes is how long, in
              seconds, a mount may go on believing what it was told: a listing and three opens
              after it cost one request instead of four. What somebody else changes on the
              server can be that many seconds out of date here. --attributes off asks again
              for every question, which is how a listing that looks stale is narrowed down.
              It can be set in the environment instead, as {Switches.Variable(CacheSwitches.LifetimeOption)}.

            Listing:
              Listing a directory costs one request, whether it holds three entries or three
              hundred. So a mount that has listed one goes on to list the directories in it
              while nobody is waiting, and opening one of those finds it already there.
              --list-ahead is how many levels below an open directory that goes.
              --list-ahead-requests is the ceiling on one round of it, counted in requests
              rather than in entries, and what a round does not get to is dropped rather than
              done later. --listings is how many listings are held at once; the ones longest
              without being proven current are let go of first. Nothing is written to disk.
              A listing is held for as long as an attribute, because it is the same request
              that says whether it still holds, so --attributes off switches this off as well.
              Each of the three takes off, and with all three off a directory is listed when
              it is opened and at no other time.
              These three can be set in the environment instead, as {Switches.Variable(DirectorySwitches.DepthOption)},
              {Switches.Variable(DirectorySwitches.RequestsOption)} and {Switches.Variable(DirectorySwitches.DirectoriesOption)}.

            Accounts:
              'account add' writes the account down and keeps its password apart from it,
              encrypted for this user on this machine. The password is asked for at a prompt,
              so that it stays out of the history of the shell.
              'account remove' withdraws the app password the login was given, unless another
              account here is signed in with the same one. A password that was typed in by
              hand is withdrawn only if you say so.
              An account is named by its id or by its uuid, and 'account list' shows both. A
              mount is written down against the uuid, so changing an id leaves it alone.
              A server may let one user in under more than one name. An account is reached
              under the name its app password was made for, which is not always the one that
              was typed. Adding a second name for a user that is here already is asked about
              first, and saying yes to it makes a second account for the same files.

            Mounts:
              'mount add' writes a mount down and asks nothing of a server. Run it afterwards
              by its name alone: what it was given is what it keeps, so a stored mount takes
              no options. 'mount list' shows what is there, and 'mount remove' takes one away
              without touching the account it was made from.
              A mount made from an account needs no --provider, --user or --anonymous: the
              account holds the server, the user and the credential. Those three belong to a
              mount made from an address, which is not written down.

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
            ProviderError.Busy => "The server is busy, or the file is held by somebody else.",
            ProviderError.Protocol => "The server answered in a way that could not be made sense of.",
            _ => "The store could not be read.",
        };

        return $"{reason} {failure.Message}";
    }
}
