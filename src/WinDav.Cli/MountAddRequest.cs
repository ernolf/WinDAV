// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Cli;

/// <summary>
/// One mount as it is to be written down, in the shape the configuration keeps it.
/// </summary>
/// <remarks>
/// The same options as a mount that is carried out, read the same way and by the same code;
/// what differs is that nothing is asked of a server, because a mount that is written down is
/// not a mount that is made. The account is named here and resolved by the command, which is
/// what turns a name into the identity the file holds. See decisions.md 73.
/// </remarks>
internal sealed class MountAddRequest
{
    private static readonly string[] s_options =
    [
        "--account",
        "--path",
        "--mount",
        "--label",
        "--icon",
        "--prefix",
        "--local",
    ];

    private MountAddRequest()
    {
    }

    /// <summary>
    /// Gets the name the mount is to be called.
    /// </summary>
    internal required string Id { get; init; }

    /// <summary>
    /// Gets the account the mount is made from, by its id or its uuid.
    /// </summary>
    internal required string Account { get; init; }

    /// <summary>
    /// Gets the path on the store that becomes the root of the mount.
    /// </summary>
    internal required string RemotePath { get; init; }

    /// <summary>
    /// Gets the drive letter the mount takes, or <see langword="null"/>.
    /// </summary>
    internal required string? DriveLetter { get; init; }

    /// <summary>
    /// Gets the directory the mount goes into, or <see langword="null"/>.
    /// </summary>
    internal required string? Directory { get; init; }

    /// <summary>
    /// Gets what the drive is called, or <see langword="null"/> to name it after what it
    /// reaches.
    /// </summary>
    internal required string? Label { get; init; }

    /// <summary>
    /// Gets the file the drive icon is taken from, or <see langword="null"/>.
    /// </summary>
    internal required string? IconPath { get; init; }

    /// <summary>
    /// Gets the network name the drive is also reached under, or <see langword="null"/> to
    /// derive one.
    /// </summary>
    internal required string? NetworkPrefix { get; init; }

    /// <summary>
    /// Gets a value indicating whether the drive appears as a local disk.
    /// </summary>
    internal required bool Local { get; init; }

    /// <summary>
    /// Reads a mount that is to be written down out of a command line.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <returns>The mount that was asked for.</returns>
    /// <exception cref="UsageException">What was typed cannot be carried out as written.</exception>
    internal static MountAddRequest Parse(CommandLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        line.EnsureOnlyKnown(s_options);

        if (line.Arguments.Count > 2)
        {
            throw new UsageException(
                $"'mount {MountCommand.Add}' writes one mount down, and '{line.Arguments[2]}' was read as a second name.");
        }

        if (line.Arguments.Count != 2)
        {
            throw new UsageException(
                $"This command needs a name for the mount, as 'mount {MountCommand.Add} <mount> --account <account>'.");
        }

        string id = EnsureItCanBeNamed(line.Arguments[1]);

        // Decision 73: a mount that is written down reaches its store through an account, and
        // there is no other way to give it one.
        string account = line.Value("--account")
            ?? throw new UsageException("A mount that is written down is made from an account, so it needs --account.");

        bool local = line.Flag("--local");
        string? prefix = MountOptions.ReadPrefix(line.Value("--prefix"));

        if (local && prefix is not null)
        {
            throw new UsageException("A mount that appears as a local disk has no network name.");
        }

        (string? driveLetter, string? directory) = MountOptions.ReadMountPoint(line.Value("--mount"));

        return new MountAddRequest
        {
            Id = id,
            Account = account,
            RemotePath = MountOptions.ReadPath(line.Value("--path")),
            DriveLetter = driveLetter,
            Directory = directory,
            Label = MountOptions.ReadLabel(line.Value("--label")),
            IconPath = MountOptions.ReadIcon(line.Value("--icon")),
            NetworkPrefix = prefix,
            Local = local,
        };
    }

    // Decision 73: the first word after "mount" is read as a verb, as an address or as the
    // name of a mount, in that order. A name that would be read as one of the other two is a
    // mount nothing could reach afterwards, so it is refused while it is still a name.
    private static string EnsureItCanBeNamed(string id)
    {
        // Without regard to case, because that is how a name reaches a mount in the file, and
        // a mount called ADD would be one that only its own spelling could get to.
        if (IsVerb(id, MountCommand.Add) || IsVerb(id, MountCommand.List) || IsVerb(id, MountCommand.Remove))
        {
            throw new UsageException($"'{id}' is what a mount is done to, so it cannot be what one is called.");
        }

        if (ServerAddress.LooksLikeOne(id))
        {
            throw new UsageException($"'{id}' is read as the address of a server, so it cannot be the name of a mount.");
        }

        return id;
    }

    private static bool IsVerb(string id, string verb) => string.Equals(id, verb, StringComparison.OrdinalIgnoreCase);
}
