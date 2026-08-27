// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;

namespace WinDav.Core;

/// <summary>
/// The product's own identity, read back out of the assembly it was built into.
/// </summary>
/// <remarks>
/// The name is written once, in <c>Directory.Build.props</c>, and MSBuild stamps it into
/// every assembly as metadata. Everything user-visible is derived from it here, so no C#
/// file spells the product out and a rename stays a change to one file. Namespaces are the
/// deliberate exception: they are written out, because deriving them would turn a rename
/// into a change to every file.
/// </remarks>
public static class ProductInfo
{
    private static readonly Assembly s_assembly = typeof(ProductInfo).Assembly;

    /// <summary>
    /// Gets the product name as it is shown to a person, for example in <c>--version</c>.
    /// </summary>
    public static string Name { get; } = Metadata("ProductName");

    /// <summary>
    /// Gets the lower-case form used wherever a machine reads the name: the configuration
    /// directory, the environment variables, later the service and the pipe.
    /// </summary>
    public static string Slug { get; } = Metadata("ProductSlug");

    /// <summary>
    /// Gets the version, without the build metadata a tag-driven build appends to it.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// Gets the name of the environment variable that overrides
    /// <see cref="ConfigurationDirectory"/>.
    /// </summary>
    public static string ConfigurationDirectoryVariable { get; } =
        $"{Slug.ToUpperInvariant()}_CONFIG_DIR";

    /// <summary>
    /// Gets the directory the configuration lives in.
    /// </summary>
    /// <remarks>
    /// The roaming application data directory with <see cref="Slug"/> below it, or whatever
    /// <see cref="ConfigurationDirectoryVariable"/> names. It is read once, when the type is
    /// first touched; a program that wants a different directory passes the path rather than
    /// changing the environment underneath itself.
    /// </remarks>
    public static string ConfigurationDirectory { get; } = ReadConfigurationDirectory();

    /// <summary>
    /// Gets the directory for what belongs to this machine alone: the credentials of the
    /// file-backed secret store, and later the cache.
    /// </summary>
    /// <remarks>
    /// The local application data directory with <see cref="Slug"/> below it, and without an
    /// override on purpose. What is kept here cannot be read on another machine, so a path
    /// that pointed into a roaming profile would only make it look as though it could.
    /// </remarks>
    public static string LocalDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Slug);

    private static string Metadata(string key)
    {
        foreach (AssemblyMetadataAttribute attribute in s_assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, key, StringComparison.Ordinal) && attribute.Value is not null)
            {
                return attribute.Value;
            }
        }

        // Not a configuration error a user could make: the item is in Directory.Build.props
        // and inherited by every project. If it is missing, the build was tampered with.
        throw new InvalidOperationException(
            $"The assembly carries no metadata named '{key}'. It is set in Directory.Build.props.");
    }

    private static string ReadVersion()
    {
        string version = s_assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? s_assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        // A tag-driven build appends '+<commit>', which belongs in a log and not in a
        // version a person reads.
        int plus = version.IndexOf('+', StringComparison.Ordinal);

        return plus < 0 ? version : version[..plus];
    }

    private static string ReadConfigurationDirectory()
    {
        string? overridden = Environment.GetEnvironmentVariable(ConfigurationDirectoryVariable);

        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Slug)
            : overridden;
    }
}
