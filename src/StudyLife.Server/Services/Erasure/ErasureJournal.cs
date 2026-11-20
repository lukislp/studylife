// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyLife.Server.Services.Erasure;

/// <summary>What the journal remembers.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ErasureKind
{
    /// <summary>An account was deleted (<see cref="Account.IAccountDeletionService"/>).</summary>
    AccountDeleted,
    /// <summary>The user withdrew (or declined after agreeing) the telemetry consent.</summary>
    TelemetryWithdrawn,
    /// <summary>The user withdrew the AI consent.</summary>
    AiConsentWithdrawn,
}

/// <summary>
/// One journal line. Deliberately nothing but what is needed to re-apply the erasure after a
/// restore: the user id, the account's creation time (the id alone is not unique over time - a
/// restored database hands out ids again - so the pair identifies "that very account" and an entry
/// can never hit a later account that reuses the id), the kind and when it happened. No name, no
/// content, no key hashes.
/// </summary>
public sealed record ErasureEntry(ErasureKind Kind, int UserId, DateTime UserCreatedAt, DateTime At);

/// <summary>
/// Append-only record of erasures and consent withdrawals that lives OUTSIDE the main database
/// (docs/ARCHITECTURE.md "Erasure journal"). A restored database backup knows nothing of what
/// happened after it was taken: it brings back deleted accounts and reverts withdrawals.
/// <see cref="ErasureReconciler"/> reads this journal after start-up and applies the entries again.
/// </summary>
public interface IErasureJournal
{
    /// <summary>False for <see cref="NullErasureJournal"/>.</summary>
    bool Enabled { get; }

    /// <summary>Appends an entry. Never throws: a failing journal must not undo or block the erasure
    /// it describes (the failure is logged).</summary>
    void Record(ErasureEntry entry);

    /// <summary>Every entry, oldest first. Unreadable lines are skipped.</summary>
    IReadOnlyList<ErasureEntry> Read();

    /// <summary>Removes entries whose <see cref="ErasureEntry.At"/> is older than
    /// <paramref name="olderThanUtc"/>. Returns how many were removed.</summary>
    int Prune(DateTime olderThanUtc);
}

/// <summary>Journal that is switched off (Postgres without <c>ErasureJournal:Path</c>).</summary>
public sealed class NullErasureJournal : IErasureJournal
{
    public bool Enabled => false;
    public void Record(ErasureEntry entry) { }
    public IReadOnlyList<ErasureEntry> Read() => [];
    public int Prune(DateTime olderThanUtc) => 0;
}

/// <summary>
/// JSON-lines file next to (not inside) the database. One small line per erasure, appended with a
/// single write so concurrent pods sharing the volume do not interleave; pruning rewrites the file
/// through a temp file and a rename.
/// </summary>
public sealed class FileErasureJournal(string path, ILogger<FileErasureJournal>? logger = null) : IErasureJournal
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();

    public string Path { get; } = path;
    public bool Enabled => true;

    public void Record(ErasureEntry entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, Json) + "\n";
            lock (_gate)
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogError(ex, "Could not append to the erasure journal at {Path}; a restore of an older backup would not be corrected for this erasure", Path);
        }
    }

    public IReadOnlyList<ErasureEntry> Read()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return [];
            string[] lines;
            using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var entries = new List<ErasureEntry>(lines.Length);
            foreach (var line in lines)
            {
                try
                {
                    if (JsonSerializer.Deserialize<ErasureEntry>(line, Json) is { } entry) entries.Add(entry);
                }
                catch (JsonException)
                {
                    logger?.LogWarning("Skipped an unreadable line in the erasure journal");
                }
            }
            return entries.OrderBy(e => e.At).ToList();
        }
    }

    public int Prune(DateTime olderThanUtc)
    {
        try
        {
            lock (_gate)
            {
                var all = Read();
                var keep = all.Where(e => e.At >= olderThanUtc).ToList();
                if (keep.Count == all.Count) return 0;

                var temp = Path + ".tmp";
                File.WriteAllText(temp, string.Concat(keep.Select(e => JsonSerializer.Serialize(e, Json) + "\n")));
                File.Move(temp, Path, overwrite: true);
                return all.Count - keep.Count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not prune the erasure journal at {Path}", Path);
            return 0;
        }
    }
}
