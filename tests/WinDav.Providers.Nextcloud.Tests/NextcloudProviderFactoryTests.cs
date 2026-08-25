// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using Xunit;

namespace WinDav.Providers.Nextcloud.Tests;

public sealed class NextcloudProviderFactoryTests
{
    private static readonly Uri s_server = new("https://cloud.example.com/");

    [Fact]
    public void TheNameIsTheOneAConfigurationWrites() =>
        Assert.Equal("nextcloud", new NextcloudProviderFactory().Name);

    [Fact]
    public void AnAccountWithAUserIdIsConnected()
    {
        NextcloudProviderFactory factory = new();

        using IStorageConnection connection = factory.Connect(new ProviderSettings
        {
            Server = s_server,
            UserId = "ernolf",
        });

        Assert.IsType<NextcloudProvider>(connection.Provider);
    }

    // A Nextcloud file path has the user in it, so there is nothing to build without one.
    [Fact]
    public void AnAccountWithoutAUserIdIsRefused()
    {
        NextcloudProviderFactory factory = new();

        Assert.Throws<ArgumentException>(() => factory.Connect(new ProviderSettings { Server = s_server }));
    }
}
