// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Providers.Nextcloud.Ocs;

// What is read out of the user the server describes. The endpoint answers with a good deal
// more than this - quota, groups, language, the address book fields - and none of it has a
// consumer here, so none of it is modelled. What is not modelled is skipped on the way in.
internal sealed class OcsUser
{
    public string? Id { get; init; }
}
