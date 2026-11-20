// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Server.Configuration;

/// <summary>
/// Where the erasure journal lives (docs/ARCHITECTURE.md "Erasure journal"). The retention of its
/// entries is <see cref="RetentionOptions.ErasureJournalDays"/>.
/// </summary>
public sealed class ErasureJournalOptions
{
    public const string SectionName = "ErasureJournal";

    /// <summary>Journal file (env ErasureJournal__Path). Unset: <c>app_data/erasure-journal.jsonl</c>
    /// next to the SQLite file; with Postgres the journal stays off unless this is set. It must be
    /// on storage that survives and is NOT rolled back together with the database (a mounted volume,
    /// shared by every replica that runs the worker), otherwise it cannot do its job.</summary>
    public string? Path { get; set; }
}
