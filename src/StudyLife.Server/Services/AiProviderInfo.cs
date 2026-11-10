// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Server.Services;

/// <summary>One model of the studylife-ai add-on, as studylife-ai's GET /internal/provider-info
/// reports it: what it is used for, who runs it, and whether that is a host the operator
/// controls.</summary>
public sealed record AiProviderRole(string Role, string Provider, string Model, bool Local);

/// <summary>Which providers and models see a user's data once AI is on. <c>AnyExternal</c> is
/// authoritative as reported (studylife-ai derives it from the roles); this server never
/// recomputes it, so the two cannot disagree about what "external" means.</summary>
public sealed record AiProviderInfo(IReadOnlyList<AiProviderRole> Roles, bool AnyExternal);
