// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using WinDav.Abstractions;
using WinDav.Core;

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

        try
        {
            return await RunAsync(args, cancellation.Token).ConfigureAwait(false);
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
            return WriteFailure(Describe(failure));
        }
        catch (InvalidOperationException unopenable)
        {
            // A credential that is where it belongs and cannot be opened, which is what one
            // written by another user or carried over from another machine looks like.
            return WriteFailure(unopenable.Message);
        }
        catch (TimeoutException expired)
        {
            // A login that was begun in the browser and not granted. The token the server
            // handed out is gone with it, and there is nothing to carry on from.
            return WriteFailure(expired.Message);
        }
        catch (Win32Exception refused)
        {
            // Windows turned the mount down, and says why better than we could.
            return WriteFailure(refused.Message);
        }
        catch (TypeInitializationException driver)
        {
            return WriteDriverProblem(driver.InnerException ?? driver);
        }
        catch (DllNotFoundException driver)
        {
            return WriteDriverProblem(driver);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C before the mount was up. Nothing was left behind.
            return Success;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        CommandLine line = CommandLine.Parse(args);

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
            return await MountCommand.RunAsync(line, cancellationToken).ConfigureAwait(false);
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
              {ProductInfo.Slug} account remove <id|uuid>
              {ProductInfo.Slug} mount <url> [options]
              {ProductInfo.Slug} help
              {ProductInfo.Slug} --version

            Options of account add:
              --provider <name>    The kind of store: nextcloud (the default) or webdav.
              --user <name>        The login name, to be asked for an app password instead of
                                   logging in through the browser. Needed for webdav.
              --anonymous          Reach the store without a credential, instead of --user.
              --id <name>          What the account is called here. Default: <login>@<server>.

            Options of mount:
              --provider <name>    The kind of store: nextcloud (the default) or webdav.
              --user <name>        The login name. Give it an app password, not the one to the account.
              --anonymous          Reach the store without a credential, instead of --user.
              --path <path>        What becomes the root of the drive. Default: the whole account.
              --mount <X:|folder>  A drive letter, or an empty folder. Default: the next free letter.
              --label <text>       What the drive is called. Default: <user>@<server>, or the folder.
              --icon <file>        The drive icon, from an .ico. Default: the one for a network drive.
              --prefix <name>      The network name, as \\server\share. Default: \\<server>\<user>.
              --local              Appear as a local disk instead of as a network drive.

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

    private static int WriteFailure(string message)
    {
        Console.Error.WriteLine(message);

        return Failed;
    }

    private static int WriteDriverProblem(Exception driver)
    {
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
