// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Fsp;
using WinDav.Abstractions;
using Xunit;

namespace WinDav.Fs.Tests;

public sealed class ProviderStatusTests
{
    [Theory]
    [InlineData(ProviderError.NotFound, FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND)]
    [InlineData(ProviderError.AlreadyExists, FileSystemBase.STATUS_OBJECT_NAME_COLLISION)]
    [InlineData(ProviderError.PermissionDenied, FileSystemBase.STATUS_ACCESS_DENIED)]
    [InlineData(ProviderError.PreconditionFailed, FileSystemBase.STATUS_SHARING_VIOLATION)]
    [InlineData(ProviderError.Conflict, FileSystemBase.STATUS_OBJECT_PATH_NOT_FOUND)]
    [InlineData(ProviderError.InsufficientStorage, FileSystemBase.STATUS_DISK_FULL)]
    [InlineData(ProviderError.Unreachable, FileSystemBase.STATUS_UNEXPECTED_NETWORK_ERROR)]
    [InlineData(ProviderError.Busy, FileSystemBase.STATUS_NETWORK_BUSY)]
    [InlineData(ProviderError.Protocol, FileSystemBase.STATUS_IO_DEVICE_ERROR)]
    [InlineData(ProviderError.Unknown, FileSystemBase.STATUS_UNEXPECTED_IO_ERROR)]
    public void EachFailureKeepsTheStatusItStandsFor(ProviderError error, int status) =>
        Assert.Equal(status, ProviderStatus.From(error));

    [Fact]
    public void NoFailureIsEverAnsweredWithSuccess()
    {
        // Also the guard for a case added to ProviderError later: an unmapped one falls to
        // the default, which still fails, and the test above is what pins its wording.
        foreach (ProviderError error in Enum.GetValues<ProviderError>())
        {
            Assert.True(ProviderStatus.From(error) < 0);
        }
    }

    [Fact]
    public void AnExceptionIsAnsweredByTheCaseItCarries()
    {
        ProviderException exception = new(ProviderError.InsufficientStorage, "No room left.");

        Assert.Equal(FileSystemBase.STATUS_DISK_FULL, ProviderStatus.From(exception));
    }

    [Fact]
    public void NothingIsNotAFailure() =>
        Assert.Throws<ArgumentNullException>(() => ProviderStatus.From((ProviderException)null!));
}
