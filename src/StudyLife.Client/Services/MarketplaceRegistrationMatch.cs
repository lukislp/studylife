// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Shared;

namespace StudyLife.Client.Services;

/// <summary>
/// Decides whether an OAuth client already registered on this server under a marketplace
/// listing's id really is that listing's add-on (2026-11-16 audit M5, id squatting). Any user can
/// register a client id first; Install must not bind to such a registration unless it is exactly
/// what the listing itself would have registered: the listing's redirect URI and nothing else (an
/// extra URI would let the registrant receive keys at a host the listing never named), and the
/// same requested access. The registration's review state is the server's business - it only
/// connects approved clients, and a registrant's later edit sends a client back to review.
/// </summary>
public static class MarketplaceRegistrationMatch
{
    public static bool Matches(MarketplaceListingDto listing, OAuthClientInfoDto registration)
    {
        if (string.IsNullOrWhiteSpace(listing.RedirectUriPattern)) return false;
        var sameRedirects = registration.RedirectUris.Count > 0
            && registration.RedirectUris.All(uri => string.Equals(uri, listing.RedirectUriPattern, StringComparison.Ordinal));
        var sameScopes = registration.RequestedScopes.ToHashSet(StringComparer.Ordinal)
            .SetEquals(listing.RequestedScopes);
        return sameRedirects && sameScopes;
    }
}
