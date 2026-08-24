// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;

namespace WinDav.Cli;

internal static class Program
{
    internal static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Assembly assembly = typeof(Program).Assembly;
        string product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "?";
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

        Console.WriteLine($"{product} {version}");
        return 0;
    }
}
