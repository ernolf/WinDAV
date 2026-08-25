// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace WinDav.Core.Configuration;

/// <summary>
/// Reads and writes the configuration file.
/// </summary>
/// <remarks>
/// A missing file is not an error; it is a client with nothing set up yet, and it reads as
/// the defaults. A file that exists but cannot be acted on is an error, and it says
/// everything that is wrong with it at once.
/// </remarks>
public sealed class ConfigurationStore
{
    /// <summary>
    /// The name of the file inside <see cref="ProductInfo.ConfigurationDirectory"/>.
    /// </summary>
    public const string FileName = "config.json";

    // The file is replaced by renaming this one over it, so a write that is interrupted
    // leaves the previous configuration whole instead of half of the new one.
    private const string TemporarySuffix = ".new";

    /// <summary>
    /// Initialises a new instance of the <see cref="ConfigurationStore"/> class.
    /// </summary>
    /// <param name="filePath">The file to read and write.</param>
    public ConfigurationStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        FilePath = filePath;
    }

    /// <summary>
    /// Gets the file this store reads and writes.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Builds a store over the file in the product's own configuration directory.
    /// </summary>
    /// <returns>A store over <see cref="FileName"/> below <see cref="ProductInfo.ConfigurationDirectory"/>.</returns>
    public static ConfigurationStore Default() =>
        new(Path.Combine(ProductInfo.ConfigurationDirectory, FileName));

    /// <summary>
    /// Reads the configuration.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// What the file holds, or a configuration of pure defaults when there is no file.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The file is not JSON, holds <c>null</c>, or describes something that cannot be acted
    /// on.
    /// </exception>
    public async Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new ClientConfiguration();
        }

        byte[] bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);

        ClientConfiguration? configuration;

        try
        {
            configuration = JsonSerializer.Deserialize(bytes, ConfigurationJson.Default.ClientConfiguration);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{FilePath} is not valid JSON.", exception);
        }

        if (configuration is null)
        {
            throw new InvalidDataException($"{FilePath} holds null where a configuration was expected.");
        }

        ConfigurationValidator.Validate(configuration, FilePath);

        return configuration;
    }

    /// <summary>
    /// Writes the configuration, replacing whatever was there.
    /// </summary>
    /// <param name="configuration">The configuration to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is in place.</returns>
    /// <exception cref="InvalidDataException">
    /// The configuration cannot be acted on. It is checked before anything is written, so a
    /// rejected save leaves the previous file untouched.
    /// </exception>
    public async Task SaveAsync(ClientConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ConfigurationValidator.Validate(configuration, FilePath);

        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = FilePath + TemporarySuffix;

        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                configuration,
                ConfigurationJson.Default.ClientConfiguration);

            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);

            File.Move(temporary, FilePath, overwrite: true);
        }
        catch
        {
            // Whatever went wrong, the half-written file is ours to take away. Deleting it
            // may itself fail, and that must not replace the failure that led here.
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // The next save overwrites it.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }

            throw;
        }
    }
}
