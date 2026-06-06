// SPDX-License-Identifier: MIT

namespace AetherNet.Extensibility;

// ─────────────────────────────────────────────────────────────────────────────
//  Biometric provider (facex integration)
//
//  FaceX (github.com/bhengubv/facex) is a zero-dependency face stack:
//  detection, 576-point 3D mesh, 512-dim embedding, recognition, anti-spoof.
//  All WebAssembly, 3ms embedding, 4ms detection.
//
//  This interface abstracts FaceX (and any future biometric backend) for use
//  by Aether services:
//    • IHandshakeService — verify physical co-presence during key exchange
//    • IVideoCallService / IGroupVideoService — live face detection
//    • AetherNetTag identity — face-bound cryptographic identity
//    • aether-market PoVToken — anti-Sybil physical presence proof
//
//  Register a platform-specific implementation (MAUI, WASM, native Android/iOS)
//  via DI. When none is registered, NullBiometricProvider is used — all
//  operations return unverified/empty results and the mesh continues normally.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A 512-dimensional L2-normalised face embedding produced by FaceX.
/// Two embeddings with <see cref="Similarity"/> above 0.30 are considered
/// the same person by FaceX's default threshold.
/// </summary>
public sealed class FaceEmbedding
{
    /// <summary>The raw 512-element embedding vector. Do not mutate.</summary>
    public float[] Vector { get; }

    /// <summary>UTC timestamp when this embedding was computed.</summary>
    public DateTimeOffset ComputedAt { get; }

    /// <param name="vector">512-element L2-normalised float array.</param>
    /// <param name="computedAt">Timestamp of computation.</param>
    public FaceEmbedding(float[] vector, DateTimeOffset computedAt)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length != 512)
            throw new ArgumentException("FaceX embeddings are exactly 512 dimensions.", nameof(vector));
        Vector     = vector;
        ComputedAt = computedAt;
    }

    /// <summary>
    /// Cosine similarity between this embedding and <paramref name="other"/>,
    /// in the range [−1, 1]. Values above 0.30 indicate the same person.
    /// </summary>
    public double Similarity(FaceEmbedding other)
    {
        ArgumentNullException.ThrowIfNull(other);
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < 512; i++)
        {
            dot   += Vector[i] * other.Vector[i];
            normA += Vector[i] * Vector[i];
            normB += other.Vector[i] * other.Vector[i];
        }
        if (normA == 0 || normB == 0) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}

/// <summary>
/// A single face detected in a frame, including bounding box, keypoints,
/// detection confidence, and the 512-dim embedding.
/// </summary>
/// <param name="X1">Left edge of bounding box in source pixel coordinates.</param>
/// <param name="Y1">Top edge of bounding box in source pixel coordinates.</param>
/// <param name="X2">Right edge of bounding box in source pixel coordinates.</param>
/// <param name="Y2">Bottom edge of bounding box in source pixel coordinates.</param>
/// <param name="DetectionScore">Detection confidence: 0.0–1.0.</param>
/// <param name="Embedding">512-dim embedding for this face.</param>
public sealed record FaceDetectionResult(
    float         X1,
    float         Y1,
    float         X2,
    float         Y2,
    float         DetectionScore,
    FaceEmbedding Embedding)
{
    /// <summary>Bounding box width in pixels.</summary>
    public float Width  => X2 - X1;

    /// <summary>Bounding box height in pixels.</summary>
    public float Height => Y2 - Y1;

    /// <summary><c>true</c> when <see cref="DetectionScore"/> exceeds the default 0.50 threshold.</summary>
    public bool IsConfident => DetectionScore >= 0.50f;
}

/// <summary>
/// Outcome of a biometric identity verification check.
/// </summary>
/// <param name="Verified">Whether the two identities are considered the same person.</param>
/// <param name="Similarity">Cosine similarity of the two embeddings (−1 to 1).</param>
/// <param name="LivenessConfirmed">
///   Whether a liveness / anti-spoof check was performed and passed.
///   <c>null</c> means liveness was not checked.
/// </param>
public sealed record BiometricVerificationResult(
    bool    Verified,
    double  Similarity,
    bool?   LivenessConfirmed = null)
{
    /// <summary>A failed verification with zero similarity.</summary>
    public static readonly BiometricVerificationResult Failed =
        new(false, 0.0, null);
}

/// <summary>
/// Extension point for face biometrics (FaceX integration).
///
/// <para>
/// Provides face detection, embedding computation, and identity verification
/// for Aether services that require physical presence or identity binding:
/// </para>
/// <list type="bullet">
///   <item><see cref="AetherNet.Handshake.IHandshakeService"/> — verify co-presence during key exchange.</item>
///   <item>Video call services — live face detection and participant tracking.</item>
///   <item>AetherNetTag — optional face-bound cryptographic identity.</item>
///   <item>aether-market <c>PoVToken</c> — anti-Sybil physical presence proof.</item>
/// </list>
///
/// <para>
/// Register a platform adapter (MAUI, WASM, native Android/iOS) via DI.
/// When no implementation is registered, <see cref="NullBiometricProvider"/>
/// is used — all methods return empty/unverified results and the mesh
/// continues normally. Biometrics are always optional; they never gate core
/// mesh connectivity.
/// </para>
///
/// <para><b>FaceX specifics:</b> embeddings are 512-dimensional, L2-normalised.
/// Similarity ≥ 0.30 = same person (FaceX default). Detection + embedding
/// combined runs in ≈7ms on a mid-range 2024 mobile device.</para>
/// </summary>
public interface IBiometricProvider
{
    /// <summary>
    /// Whether biometric hardware and the FaceX engine are available on this device.
    /// When <c>false</c>, all other methods return safe neutral values.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Detect faces in a raw RGB frame and compute their embeddings in one pass.
    /// </summary>
    /// <param name="rgbHwc">
    ///   Raw image data: width × height × 3 bytes, HWC layout, values 0–255.
    /// </param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="maxFaces">Maximum number of faces to return. Defaults to 4.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Detected faces with embeddings, sorted by detection confidence descending.</returns>
    Task<IReadOnlyList<FaceDetectionResult>> DetectAsync(
        byte[]            rgbHwc,
        int               width,
        int               height,
        int               maxFaces           = 4,
        CancellationToken cancellationToken  = default)
        => Task.FromResult<IReadOnlyList<FaceDetectionResult>>([]);

    /// <summary>
    /// Compute a 512-dim embedding from a pre-aligned 112×112 face crop.
    /// Use <see cref="DetectAsync"/> when you have an unaligned frame.
    /// </summary>
    /// <param name="alignedRgb">112×112×3 float32 array, HWC layout, values in [−1, 1].</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FaceEmbedding?> EmbedAsync(
        float[]           alignedRgb,
        CancellationToken cancellationToken = default)
        => Task.FromResult<FaceEmbedding?>(null);

    /// <summary>
    /// Verify whether two embeddings represent the same person.
    /// </summary>
    /// <param name="a">Reference embedding (e.g. stored in AetherNetTag identity).</param>
    /// <param name="b">Probe embedding (e.g. from a live camera frame).</param>
    /// <param name="threshold">
    ///   Cosine similarity threshold for a positive match. Defaults to 0.30
    ///   (FaceX recommended default).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<BiometricVerificationResult> VerifyAsync(
        FaceEmbedding     a,
        FaceEmbedding     b,
        double            threshold          = 0.30,
        CancellationToken cancellationToken  = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var similarity = a.Similarity(b);
        return Task.FromResult(new BiometricVerificationResult(
            Verified:           similarity >= threshold,
            Similarity:         similarity,
            LivenessConfirmed:  null));
    }
}

/// <summary>
/// No-op <see cref="IBiometricProvider"/> — used when biometric hardware is
/// absent or FaceX is not loaded. All operations return safe neutral values.
/// </summary>
public sealed class NullBiometricProvider : IBiometricProvider
{
    /// <summary>The singleton instance.</summary>
    public static readonly NullBiometricProvider Instance = new();

    private NullBiometricProvider() { }

    /// <inheritdoc/>
    public bool IsAvailable => false;
}
