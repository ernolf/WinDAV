// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace WinDav.Fs;

// Explorer reads what a drive is called and how it looks once, and then remembers. This is
// the call that tells it to look again. Without it a change appears whenever Explorer next
// refreshes on its own, which is not a length of time anybody can name.
internal static partial class ShellNotification
{
    // SHCNE_ASSOCCHANGED, the coarsest of the events and the one that reaches the drive
    // views. The finer ones are about a single item and take a path; there is no event that
    // says only "the name of this drive changed".
    private const int AssociationsChanged = 0x08000000;

    // SHCNF_IDLIST, which describes what the two item arguments would be. There are none
    // here, and this event takes none.
    private const uint ItemIdList = 0x0000;

    internal static void DrivesChanged() => SHChangeNotify(AssociationsChanged, ItemIdList, 0, 0);

    [LibraryImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
