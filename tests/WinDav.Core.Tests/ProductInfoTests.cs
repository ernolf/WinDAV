// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core.Configuration;
using Xunit;

namespace WinDav.Core.Tests;

public sealed class ProductInfoTests
{
    // The product name is deliberately not written out here either. What is under test is
    // that the identity arrives from the build and hangs together, not what it says today.
    [Fact]
    public void TheNameArrivesFromTheBuild() => Assert.False(string.IsNullOrWhiteSpace(ProductInfo.Name));

    [Fact]
    public void TheSlugIsTheNameWithoutItsCapitals()
    {
        Assert.Equal(ProductInfo.Name.ToUpperInvariant(), ProductInfo.Slug.ToUpperInvariant());
        Assert.False(ProductInfo.Slug.Any(char.IsUpper), $"The slug '{ProductInfo.Slug}' has capitals in it.");
    }

    [Fact]
    public void TheVersionCarriesNoBuildMetadata()
    {
        Assert.False(string.IsNullOrWhiteSpace(ProductInfo.Version));
        Assert.False(ProductInfo.Version.Contains('+', StringComparison.Ordinal));
    }

    [Fact]
    public void TheEnvironmentVariableIsBuiltFromTheSlug()
    {
        Assert.StartsWith(
            ProductInfo.Slug.ToUpperInvariant(),
            ProductInfo.ConfigurationDirectoryVariable,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheConfigurationDirectoryIsNamedAfterTheSlug() =>
        Assert.Equal(ProductInfo.Slug, Path.GetFileName(ProductInfo.ConfigurationDirectory));

    [Fact]
    public void TheDefaultStoreSitsInTheConfigurationDirectory()
    {
        ConfigurationStore store = ConfigurationStore.Default();

        Assert.Equal(ProductInfo.ConfigurationDirectory, Path.GetDirectoryName(store.FilePath));
        Assert.Equal(ConfigurationStore.FileName, Path.GetFileName(store.FilePath));
    }
}
