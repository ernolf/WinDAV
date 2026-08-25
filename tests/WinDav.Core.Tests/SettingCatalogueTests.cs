// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using WinDav.Core.Configuration;
using Xunit;

namespace WinDav.Core.Tests;

// The catalogue is written by hand. These tests are what stops it from quietly falling
// behind the model it describes.
public sealed class SettingCatalogueTests
{
    [Fact]
    public void EverySettingInTheModelIsDescribed()
    {
        foreach (string path in ModelPaths())
        {
            Assert.True(
                SettingCatalogue.Find(path) is not null,
                $"'{path}' exists in the model but the catalogue says nothing about it.");
        }
    }

    [Fact]
    public void NoDescriptionOutlivesItsSetting()
    {
        HashSet<string> model = new(ModelPaths(), StringComparer.Ordinal);

        foreach (SettingDescriptor descriptor in SettingCatalogue.All)
        {
            Assert.True(
                model.Contains(descriptor.Path),
                $"The catalogue describes '{descriptor.Path}', which is nowhere in the model.");
        }
    }

    [Fact]
    public void NoPathIsDescribedTwice() =>
        Assert.Distinct(SettingCatalogue.All.Select(descriptor => descriptor.Path), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void EveryDescriptionIsFilledIn()
    {
        foreach (SettingDescriptor descriptor in SettingCatalogue.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Path));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary), $"{descriptor.Path} has no summary.");
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Effect), $"{descriptor.Path} says nothing about its effect.");
            Assert.False(string.IsNullOrWhiteSpace(descriptor.DefaultValue), $"{descriptor.Path} has no default.");
        }
    }

    // Whole sentences, because these end up in front of a person who is deciding what to set.
    [Fact]
    public void SummaryAndEffectAreSentences()
    {
        foreach (SettingDescriptor descriptor in SettingCatalogue.All)
        {
            Assert.EndsWith(".", descriptor.Summary, StringComparison.Ordinal);
            Assert.EndsWith(".", descriptor.Effect, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FindDoesNotCareAboutCase()
    {
        SettingDescriptor? descriptor = SettingCatalogue.Find("MOUNTS[].DRIVELETTER");

        Assert.NotNull(descriptor);
        Assert.Equal("mounts[].driveLetter", descriptor.Path);
    }

    [Fact]
    public void FindHasNothingToSayAboutASettingThatIsNotThere() =>
        Assert.Null(SettingCatalogue.Find("mounts[].colour"));

    // The paths the file would have, derived the way the serialiser derives its names.
    private static IEnumerable<string> ModelPaths()
    {
        foreach (PropertyInfo property in typeof(ClientConfiguration).GetProperties())
        {
            yield return CamelCase(property.Name);
        }

        foreach (PropertyInfo property in typeof(AccountConfiguration).GetProperties())
        {
            yield return $"accounts[].{CamelCase(property.Name)}";
        }

        foreach (PropertyInfo property in typeof(MountConfiguration).GetProperties())
        {
            yield return $"mounts[].{CamelCase(property.Name)}";
        }
    }

    private static string CamelCase(string name) => $"{char.ToLowerInvariant(name[0])}{name[1..]}";
}
