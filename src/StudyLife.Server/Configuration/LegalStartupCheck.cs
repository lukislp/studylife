// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StudyLife.Server.Data;

namespace StudyLife.Server.Configuration;

/// <summary>
/// Audit 2026-11-19 S3: without <c>Legal:PrivacyUrl</c> and <c>Legal:ImprintUrl</c> every page links
/// the publisher's privacy policy and imprint (labelled as the app publisher's). That is correct for
/// the publisher's own instance, but an operator who serves other people owes them an imprint and
/// privacy policy of their own. Once more than one account exists and neither setting is set, this
/// logs one warning at startup naming the settings. A warning rather than an error: existing
/// installations keep starting unchanged.
/// </summary>
public sealed class LegalStartupCheck(
    IOptions<LegalOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<LegalStartupCheck> logger) : IHostedService
{
    internal const string Warning =
        "More than one account exists on this server but neither Legal:PrivacyUrl nor Legal:ImprintUrl " +
        "is set, so the pages link the app publisher's privacy policy and imprint instead of yours. " +
        "If you operate this server for other people, set Legal__PrivacyUrl and Legal__ImprintUrl " +
        "(see docs/operator-privacy-template.md).";

    /// <summary>True when the warning applies: several accounts and no operator legal page at all.</summary>
    internal static bool ShouldWarn(LegalOptions legal, int accounts) =>
        accounts > 1
        && LegalOptions.Normalize(legal.PrivacyUrl) is null
        && LegalOptions.Normalize(legal.ImprintUrl) is null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Settings first: no database round trip for an operator who configured them.
        if (ShouldWarn(options.Value, int.MaxValue) is false) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            var accounts = await db.AuthUsers.AsNoTracking().Take(2).CountAsync(cancellationToken);
            if (ShouldWarn(options.Value, accounts))
                logger.LogWarning("{LegalWarning}", Warning);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A check that cannot read the database (not migrated yet, unavailable) must never stop the start.
            logger.LogDebug(e, "The legal-pages startup check could not count accounts");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
