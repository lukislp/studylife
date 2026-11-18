// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Shared;

namespace StudyLife.Client.Services;

/// <summary>
/// Additive hook for native-only telemetry (same pattern as INativeHealthData/INativePush/
/// INativeAppAuth): app launch timing, MetricKit crash/resource reports and HealthKit query
/// outcomes never exist in the browser - the browser client registers <see cref="NoNativeTelemetry"/>
/// and TelemetryService.FlushAsync simply merges nothing extra into its batches.
///
/// <see cref="TelemetryEventDto"/> (StudyLife.Shared, the same shape POST /api/telemetry sends
/// over the wire) is reused here rather than inventing a parallel type - it is already a plain
/// class, not a value tuple, so it doesn't trip the Mono AOT LINQ-over-tuple crash documented on
/// <see cref="CardioFitnessPoint"/>, and duplicating an already-non-tuple shape only to satisfy
/// the letter of "a small sealed class" would be premature abstraction for no gain.
/// </summary>
public interface INativeTelemetry
{
    bool IsAvailable => false;

    /// <summary>Drains every native event (MetricKit launch/resource/crash reports, HealthKit
    /// query outcomes, native push lifecycle) collected since the last call. Null/empty when
    /// there's nothing new - TelemetryService calls this once per flush and merges the result
    /// into the outgoing batch.</summary>
    Task<IReadOnlyList<TelemetryEventDto>?> DrainAsync() => Task.FromResult<IReadOnlyList<TelemetryEventDto>?>(null);

    /// <summary>Consent contract for the native host (TDDDG 25: nothing telemetry-related may be
    /// stored on the device without consent). The native host must not persist native telemetry
    /// events (MetricKit/HealthKit/push lifecycle queue files) unless the LAST call was
    /// <c>granted: true</c>, and must delete its queued native telemetry (queue files) when called
    /// with <c>false</c>. TelemetryService calls this once as soon as the consent is known at
    /// startup (an undecided consent counts as <c>false</c>), again on every change of the
    /// effective consent, and with <c>false</c> on sign-out. Default no-op so hosts built before
    /// this contract existed still compile.</summary>
    Task ApplyConsentAsync(bool granted) => Task.CompletedTask;
}

/// <summary>Default registration in the browser client (Program.cs).</summary>
public sealed class NoNativeTelemetry : INativeTelemetry
{
}
