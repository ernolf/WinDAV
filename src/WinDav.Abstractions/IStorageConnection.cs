// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// A provider together with whatever had to be built to reach its store.
/// </summary>
/// <remarks>
/// <see cref="IStorageProvider"/> deliberately says nothing about lifetime: it is a set of
/// operations, and an operation does not get closed. What does get closed is the connection
/// underneath it — sockets, handlers, whatever a transport needs — and that is this.
/// </remarks>
public interface IStorageConnection : IDisposable
{
    /// <summary>
    /// Gets the provider. It is usable until this connection is disposed, and not after.
    /// </summary>
    IStorageProvider Provider { get; }
}
