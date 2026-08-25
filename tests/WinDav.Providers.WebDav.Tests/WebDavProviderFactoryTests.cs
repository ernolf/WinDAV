// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using Xunit;

namespace WinDav.Providers.WebDav.Tests;

public sealed class WebDavProviderFactoryTests
{
    private static readonly Uri s_server = new("https://dav.example.com/store/");

    [Fact]
    public void TheNameIsTheOneAConfigurationWrites() =>
        Assert.Equal("webdav", new WebDavProviderFactory().Name);

    [Fact]
    public void TheServerAddressIsAllThatIsNeeded()
    {
        WebDavProviderFactory factory = new();

        using IStorageConnection connection = factory.Connect(new ProviderSettings { Server = s_server });

        Assert.IsType<WebDavProvider>(connection.Provider);
    }
}
