// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace WinDav.Fs;

/// <summary>
/// What the shell has registered to draw over icons, read the way Windows reads it.
/// </summary>
/// <remarks>
/// <para>
/// An overlay handler is loaded into <c>explorer.exe</c> once per class and asked about
/// whatever folder a window shows, this mount included. It has no scope of its own, so nothing
/// here belongs to a drive and nothing here is the mount's to change: reading it is the only
/// way a probe that carries no process id gets a name at all.
/// </para>
/// <para>
/// Windows loads the first <see cref="Loads"/> in ordinal order of the key names and ignores
/// the rest, which is why vendors pad their names with leading spaces. The sort is ordinal for
/// exactly that reason: a culture-aware comparison weighs those spaces differently and would
/// put the line in the wrong place. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#84-the-mount-says-who-walked-it-and-what-the-shell-has-registered">decision 84</see>.
/// </para>
/// </remarks>
public static class ShellOverlays
{
    /// <summary>
    /// How many overlay handlers Windows loads. The rest are registered and never asked.
    /// </summary>
    public const int Loads = 15;

    private const string Identifiers =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";

    /// <summary>
    /// Reads the registered overlay handlers, in the order Windows goes through them.
    /// </summary>
    /// <returns>
    /// What is registered, machine-wide and for this user, with the ones Windows has room for
    /// first. Empty where nothing is registered or the keys cannot be read.
    /// </returns>
    public static IReadOnlyList<ShellOverlay> Read()
    {
        Dictionary<string, string> registered = new(StringComparer.Ordinal);

        // Machine first, then the user's own: Explorer sees both, and a name that is in both
        // is one handler and not two.
        Collect(Registry.LocalMachine, registered);
        Collect(Registry.CurrentUser, registered);

        List<string> names = [.. registered.Keys];

        names.Sort(StringComparer.Ordinal);

        List<ShellOverlay> overlays = new(names.Count);

        for (int index = 0; index < names.Count; index++)
        {
            string clsid = registered[names[index]];
            string? module = ModuleOf(clsid);
            bool? present = null;
            string? vendor = null;

            // Only a full path can be looked for. A server registered by its bare name is one
            // Windows finds through the search path of whatever process loads it, and this
            // process is not that one, so there is nothing here to look for it with.
            if (module is not null && Path.IsPathFullyQualified(module))
            {
                bool found = File.Exists(module);

                present = found;

                if (found)
                {
                    vendor = VendorOf(module);
                }
            }

            overlays.Add(new ShellOverlay(names[index], clsid, module, vendor, present, index < Loads));
        }

        return overlays;
    }

    private static void Collect(RegistryKey hive, Dictionary<string, string> registered)
    {
        try
        {
            using RegistryKey? key = hive.OpenSubKey(Identifiers);

            if (key is null)
            {
                return;
            }

            foreach (string name in key.GetSubKeyNames())
            {
                using RegistryKey? entry = key.OpenSubKey(name);

                // The class is the default value of the key. One that holds anything else is
                // registered wrong, and Explorer skips it too.
                if (entry?.GetValue(null) is string clsid && clsid.Length > 0)
                {
                    registered.TryAdd(name, clsid);
                }
            }
        }
        catch (SecurityException)
        {
            // Not readable is not a reason to bring a mount down over a report.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // A 32-bit handler keeps its server under WOW6432Node, and Windows finds it there through
    // the view rather than through a second path.
    private static string? ModuleOf(string clsid) =>
        ServerOf(clsid, RegistryView.Registry64) ?? ServerOf(clsid, RegistryView.Registry32);

    private static string? ServerOf(string clsid, RegistryView view)
    {
        try
        {
            using RegistryKey classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using RegistryKey? server = classes.OpenSubKey($@"CLSID\{clsid}\InprocServer32");

            if (server is null)
            {
                return null;
            }

            // A handler written against the runtime registers the runtime's own shim as its
            // server and names its assembly beside it. The shim is Microsoft's and says so,
            // so reading the default value alone would put Microsoft's name on somebody
            // else's handler.
            if (server.GetValue("CodeBase") is string assembly && assembly.Length > 0)
            {
                return Cleaned(assembly);
            }

            return server.GetValue(null) is string path && path.Length > 0 ? Cleaned(path) : null;
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A code base may stand as a path or as a file URI, both of which are registered in the
    // wild. They name the same file and only one of the two can be handed to the file system.
    private static string Cleaned(string value)
    {
        string path = Environment.ExpandEnvironmentVariables(value.Trim('"'));

        return path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(path, UriKind.Absolute, out Uri? uri)
            && uri.IsFile
                ? uri.LocalPath
                : path;
    }

    private static string? VendorOf(string module)
    {
        try
        {
            string? company = FileVersionInfo.GetVersionInfo(module).CompanyName;

            return string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        }
        catch (IOException)
        {
            // A file that is there and cannot be read says nothing about itself, and the path
            // still names the program that installed it.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
