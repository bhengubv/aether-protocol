// SPDX-License-Identifier: MIT

using AetherMesh.Extensibility;

namespace AetherMesh.Core.Tests.Fakes;

/// <summary>
/// Configurable <see cref="IBiometricProvider"/> fake for unit testing.
///
/// <para>
/// By default the provider is available and returns no detected faces.
/// Call <see cref="SetDetectionResult"/> / <see cref="SetVerifyResult"/> to
/// stage a specific scenario before the test act.
/// </para>
/// </summary>
internal sealed class FakeBiometricProvider : IBiometricProvider
{
    // ── Configuration ──────────────────────────────────────────────────────────

    /// <summary>Simulates hardware/engine availability. Default: <c>true</c>.</summary>
    public bool IsAvailable { get; set; } = true;

    private FaceDetectionResult? _fixedDetection;
    private bool   _verifyVerified;
    private double _verifySimilarity;

    // ── Setup helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Set the face that <see cref="DetectAsync"/> will return.
    /// Pass <c>null</c> to simulate "no face detected" (returns empty list).
    /// </summary>
    public void SetDetectionResult(FaceDetectionResult? result) =>
        _fixedDetection = result;

    /// <summary>
    /// Set what <see cref="VerifyAsync"/> reports.
    /// </summary>
    public void SetVerifyResult(bool verified, double similarity)
    {
        _verifyVerified   = verified;
        _verifySimilarity = similarity;
    }

    // ── IBiometricProvider ────────────────────────────────────────────────────

    public Task<IReadOnlyList<FaceDetectionResult>> DetectAsync(
        byte[]            rgbHwc,
        int               width,
        int               height,
        int               maxFaces           = 4,
        CancellationToken cancellationToken  = default)
    {
        IReadOnlyList<FaceDetectionResult> list =
            _fixedDetection is null ? [] : [_fixedDetection];
        return Task.FromResult(list);
    }

    public Task<BiometricVerificationResult> VerifyAsync(
        FaceEmbedding     a,
        FaceEmbedding     b,
        double            threshold          = 0.30,
        CancellationToken cancellationToken  = default)
        => Task.FromResult(new BiometricVerificationResult(_verifyVerified, _verifySimilarity));
}
