// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Fsp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinDav.Abstractions;

namespace WinDav.Fs;

/// <summary>
/// A store, mounted.
/// </summary>
/// <remarks>
/// <para>
/// The mount lasts as long as this object does. Disposing it takes the drive away, and so
/// does the process ending, because WinFsp notices the owner is gone.
/// </para>
/// <para>
/// Everything here needs the WinFsp driver to be installed. Touching this type without it
/// throws while the library is being loaded, which is the earliest and clearest place for
/// that to happen.
/// </para>
/// </remarks>
public sealed class ProviderMount : IDisposable
{
    // Explorer keeps the name and the icon of a drive in two caches of its own and fills them
    // when it sees fit, so both have to be written again whenever something takes them away.
    // Five seconds is under what a person notices and costs a registry read when there is
    // nothing to do.
    private static readonly TimeSpan s_tickInterval = TimeSpan.FromSeconds(5);

    private readonly FileSystemHost _host;
    private readonly MountSettings _settings;
    private readonly ILogger _log;

    // Held while the branding is written or taken away, because the tick runs on a thread of
    // the pool and disposing is what it races with.
    private readonly Lock _gate = new();

    private MountBranding? _branding;
    private Timer? _tick;
    private bool _mounted;
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance of the <see cref="ProviderMount"/> class. Nothing is
    /// mounted until <see cref="Mount"/> is called.
    /// </summary>
    /// <param name="provider">The store to show.</param>
    /// <param name="settings">Where the mount appears and how it presents itself.</param>
    /// <param name="loggerFactory">
    /// Where a mount going up and coming down is written down, and with it everything the
    /// file system underneath was asked for. Nothing is written without one, which is what a
    /// test that only wants a mount asks for.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ProviderMount(
        IStorageProvider provider,
        MountSettings settings,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _host = new FileSystemHost(new WinDavFileSystem(provider, settings, loggerFactory));
        _log = loggerFactory?.CreateLogger(typeof(ProviderMount)) ?? NullLogger.Instance;
    }

    /// <summary>
    /// Gets the version of the installed WinFsp driver.
    /// </summary>
    /// <remarks>
    /// The binding this was built against and the driver on the machine have to share a
    /// major version, and the driver may not be the older of the two in its minor. Asking
    /// before mounting turns "the mount failed" into a sentence naming the version to
    /// install.
    /// </remarks>
    public static Version DriverVersion => FileSystemHost.Version();

    /// <summary>
    /// Gets where the mount is, once it has one, and <see langword="null"/> before that.
    /// </summary>
    /// <remarks>
    /// This is the drive letter or directory Windows settled on, which is what to show a
    /// person: a mount asked for the next free letter does not know its own until it has it.
    /// </remarks>
    public string? MountPoint => _host.MountPoint();

    /// <summary>
    /// Puts the mount in place.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The mount has been disposed.</exception>
    /// <exception cref="Win32Exception">
    /// Windows refused the mount. The message is the one Windows itself gives for the
    /// reason, for example that the drive letter is taken.
    /// </exception>
    public void Mount()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Asked first, because a mount point that cannot be had fails here with the reason
        // rather than halfway through building a file system.
        Check(_host.Preflight(_settings.MountPoint));
        Check(_host.Mount(_settings.MountPoint));

        // Not before: the drive letter an icon hangs on is the one Windows settled on, and a
        // mount that asked for the next free letter does not know it until it has it.
        _branding = new MountBranding(_settings, _host.MountPoint());

        _branding.Ensure();

        _tick = new Timer(_ => Tick(), null, s_tickInterval, s_tickInterval);
        _mounted = true;

        // Decision 74 has this always on: a mount that is up and one that is gone are the two
        // things a person asks about afterwards, and neither leaves any other trace. Asked
        // first all the same, so that what the arguments cost is not paid by a log that is
        // switched off (CA1873).
        if (_log.IsEnabled(LogLevel.Information))
        {
            _log.LogInformation(
                "Mount {Label} is up at {MountPoint} over WinFsp {Driver}.",
                _settings.VolumeLabel,
                _host.MountPoint(),
                DriverVersion);
        }
    }

    /// <summary>
    /// Takes the mount away. What is done afterwards to a file that was open on it is
    /// Windows' business, and it answers that the volume was removed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _tick?.Dispose();

        // Before the volume goes, while the drive letter is still this mount's. A tick that
        // was already running holds the gate, so what it writes is taken away and not the
        // other way round.
        lock (_gate)
        {
            _branding?.Remove();
            _branding = null;
        }

        // Asked while the mount still has one: the host answers null once it is disposed.
        string? mountPoint = _host.MountPoint();

        _host.Dispose();

        if (_mounted && _log.IsEnabled(LogLevel.Information))
        {
            _log.LogInformation("Mount {Label} at {MountPoint} is gone.", _settings.VolumeLabel, mountPoint);
        }

        GC.SuppressFinalize(this);
    }

    private void Tick()
    {
        lock (_gate)
        {
            _branding?.Ensure();
        }
    }

    private static void Check(int status)
    {
        if (status >= 0)
        {
            return;
        }

        // The status is WinFsp's, the message is Windows'. Wrapping it in an exception of
        // our own would mean writing sentences Windows has already written better.
        throw new Win32Exception((int)FileSystemBase.Win32FromNtStatus(status));
    }
}
