// SPDX-License-Identifier: MIT
#if ANDROID
using System.Runtime.Versioning;
using Android.App;
using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;
using AetherNetNodeService.Client;

namespace AetherNetNodeService.Android;

/// <summary>
/// The Android <see cref="IOwnerCheck"/>: the phone's own screen asks the person holding it to prove they own it.
/// On Android 10 and later that is the system's fingerprint sheet, with the phone's PIN, pattern or password as
/// the way round it; on Android 7 to 9, the lock screen's own confirm screen. Either way it is Android's screen,
/// not one of ours — AetherNetService still has no UI, and the app asking shows nothing of its own here.
/// </summary>
/// <remarks>
/// The app using this declares <c>android.permission.USE_BIOMETRIC</c> in its own manifest — an install-time
/// permission, nothing for the person to grant. Without it Android refuses the sheet outright ("Must have
/// USE_BIOMETRIC permission"), and this falls back to the lock screen's own check. It is not declared on this
/// library because AetherNetService uses the library too, and a service with no screen has no use for it.
/// </remarks>
public sealed class AndroidOwnerCheck : IOwnerCheck
{
    /// <summary>The activity result code the Android 7–9 path answers on. Distinct from the app's file picker.</summary>
    public const int RequestCode = 0x0A38;

    private const string Title = "Confirm it's you";

    private static TaskCompletionSource<OwnerCheck>? _waitingForResult;

    private readonly Func<Activity?> _currentActivity;

    /// <param name="currentActivity">The activity on screen right now — the check is shown on top of it.</param>
    public AndroidOwnerCheck(Func<Activity?> currentActivity)
    {
        _currentActivity = currentActivity ?? throw new ArgumentNullException(nameof(currentActivity));
    }

    public Task<OwnerCheck> ConfirmAsync(string reason, CancellationToken cancellationToken = default)
    {
        var activity = _currentActivity() ?? throw new InvalidOperationException("There is no screen to ask on.");

        // No screen lock means nothing to prove ownership with — refused, not waved through.
        if (activity.GetSystemService(Context.KeyguardService) is not KeyguardManager { IsDeviceSecure: true } keyguard)
            return Task.FromResult(OwnerCheck.NoScreenLock);

        var answer = new TaskCompletionSource<OwnerCheck>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RunOnUiThread(() =>
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    try
                    {
                        AskWithSystemSheet(activity, reason, answer, cancellationToken);
                        return;
                    }
                    catch (Exception)
                    {
                        // The sheet would not start on this phone — the lock screen's own check still will.
                    }
                }

                AskWithLockScreen(activity, keyguard, reason, answer);
            }
            catch (Exception ex)
            {
                answer.TrySetException(ex);
            }
        });
        return answer.Task;
    }

    /// <summary>
    /// Hand the activity's result here. Android 9 and older answer the confirm screen through the activity, not a
    /// callback, so the activity forwards it. True when it was this check's result.
    /// </summary>
    public static bool OnActivityResult(int requestCode, Result resultCode)
    {
        if (requestCode != RequestCode) return false;
        Interlocked.Exchange(ref _waitingForResult, null)
            ?.TrySetResult(resultCode == Result.Ok ? OwnerCheck.Confirmed : OwnerCheck.NotConfirmed);
        return true;
    }

    [SupportedOSPlatform("android29.0")]
    private static void AskWithSystemSheet(Activity activity, string reason, TaskCompletionSource<OwnerCheck> answer, CancellationToken cancellationToken)
    {
        var builder = new BiometricPrompt.Builder(activity)
            .SetTitle(Title)
            .SetDescription(reason);

        // Fingerprint or face if the phone has one, and the phone's own PIN, pattern or password either way.
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            builder.SetAllowedAuthenticators((int)(BiometricManagerAuthenticators.BiometricWeak | BiometricManagerAuthenticators.DeviceCredential));
        else
            builder.SetDeviceCredentialAllowed(true);

        var cancel = new CancellationSignal();
        cancellationToken.Register(() =>
        {
            cancel.Cancel();
            answer.TrySetResult(OwnerCheck.NotConfirmed);
        });

        builder.Build().Authenticate(cancel, activity.MainExecutor!, new SheetAnswer(answer));
    }

    private static void AskWithLockScreen(Activity activity, KeyguardManager keyguard, string reason, TaskCompletionSource<OwnerCheck> answer)
    {
#pragma warning disable CA1422 // the Android 7–9 path, which is exactly where this call is the one that exists
        var intent = keyguard.CreateConfirmDeviceCredentialIntent(Title, reason);
#pragma warning restore CA1422
        if (intent is null)
        {
            answer.TrySetResult(OwnerCheck.NoScreenLock);
            return;
        }

        // One check at a time; a second one replaces the first, which then counts as not confirmed.
        Interlocked.Exchange(ref _waitingForResult, answer)?.TrySetResult(OwnerCheck.NotConfirmed);
        activity.StartActivityForResult(intent, RequestCode);
    }

    [SupportedOSPlatform("android28.0")]
    private sealed class SheetAnswer : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<OwnerCheck> _answer;

        public SheetAnswer(TaskCompletionSource<OwnerCheck> answer) => _answer = answer;

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result)
            => _answer.TrySetResult(OwnerCheck.Confirmed);

        // Cancelled, backed out of, or locked out after too many tries. One bad fingerprint is not an answer —
        // the sheet stays up for another go — so OnAuthenticationFailed is left alone.
        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString)
            => _answer.TrySetResult(OwnerCheck.NotConfirmed);
    }
}
#endif
