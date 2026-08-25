// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;

namespace WinDav.Core.Providers;

/// <summary>
/// The provider factories this program was given, by the name they are written under in a
/// configuration.
/// </summary>
/// <remarks>
/// This is the whole reason the core does not reference a single provider. It holds
/// factories it was handed and looks them up by name; what they build, it never learns. The
/// program that runs is the one place where a name and a provider meet.
/// </remarks>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IStorageProviderFactory> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initialises a new instance of the <see cref="ProviderRegistry"/> class.
    /// </summary>
    /// <param name="factories">The factories to hold.</param>
    /// <exception cref="ArgumentException">
    /// Two factories claim the same name. Which one would win is not something to decide by
    /// the order they arrived in.
    /// </exception>
    public ProviderRegistry(IEnumerable<IStorageProviderFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);

        foreach (IStorageProviderFactory factory in factories)
        {
            ArgumentNullException.ThrowIfNull(factory);

            if (!_factories.TryAdd(factory.Name, factory))
            {
                throw new ArgumentException(
                    $"Two providers are registered under the name '{factory.Name}'.",
                    nameof(factories));
            }
        }

        Names = [.. _factories.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Gets the registered names, in order.
    /// </summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>
    /// Finds the factory for a name, ignoring case.
    /// </summary>
    /// <param name="name">The name as it appears in the configuration.</param>
    /// <returns>The factory.</returns>
    /// <exception cref="InvalidDataException">
    /// No factory is registered under that name. The message lists the ones that are: a
    /// misspelt provider is the likeliest way to get here, and the answer is usually
    /// visible in the list.
    /// </exception>
    public IStorageProviderFactory Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_factories.TryGetValue(name, out IStorageProviderFactory? factory))
        {
            return factory;
        }

        throw new InvalidDataException(
            $"There is no provider named '{name}'. This build knows: {string.Join(", ", Names)}.");
    }
}
