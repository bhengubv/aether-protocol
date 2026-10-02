// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using AetherNetNodeService.Client;

namespace AetherNetNodeService.Android;

/// <summary>
/// The Android <see cref="INodePackageInstaller"/>: hands AetherNetService's package to the phone's own installer,
/// through an install session, and the person confirms the install on the phone's own screen.
/// </summary>
/// <remarks>
/// <para>
/// The app asking needs <c>REQUEST_INSTALL_PACKAGES</c> in its manifest, and the person's leave to install apps from
/// it, which the phone asks for itself the first time. Nothing is installed without that screen: an app cannot
/// install another quietly, and should not.
/// </para>
/// <para>
/// A session rather than a file handed to a viewer: no file provider, nothing left readable on disk, and the phone
/// says how the install ended.
/// </para>
/// </remarks>
public sealed class AndroidNodePackageInstaller : INodePackageInstaller
{
    private readonly Context _context;
    private readonly Action<string>? _log;

    /// <param name="context">An application context.</param>
    /// <param name="log">Where to say how each install went; logcat is the caller's choice.</param>
    public AndroidNodePackageInstaller(Context context, Action<string>? log = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _log = log;
    }

    /// <inheritdoc />
    /// <remarks>True once the phone's installer has been handed the package; the person finishes it there.</remarks>
    public async Task<bool> RequestInstallAsync(byte[] packageBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        if (packageBytes.Length == 0) return false;

        PackageInstaller.Session? session = null;
        try
        {
            var installer = _context.PackageManager!.PackageInstaller;
            var id = installer.CreateSession(new PackageInstaller.SessionParams(PackageInstallMode.FullInstall));
            session = installer.OpenSession(id);

            using (var into = session.OpenWrite("aethernetservice", 0, packageBytes.Length))
            {
                await into.WriteAsync(packageBytes, cancellationToken).ConfigureAwait(false);
                session.Fsync(into);
            }

            // The phone answers by broadcast: first "the person has to confirm", with the screen to show; then how the
            // install ended. Only this app can send to the receiver — the answer comes through its own pending intent.
            var action = $"{_context.PackageName}.AETHERNETSERVICE_INSTALL.{id}";
            var receiver = new Answer(this, action);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                _context.RegisterReceiver(receiver, new IntentFilter(action), ReceiverFlags.NotExported);
            else
                _context.RegisterReceiver(receiver, new IntentFilter(action));

            var flags = PendingIntentFlags.UpdateCurrent;
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) flags |= PendingIntentFlags.Mutable;   // the phone fills in the answer
            var answer = PendingIntent.GetBroadcast(_context, id, new Intent(action).SetPackage(_context.PackageName), flags)!;

            session.Commit(answer.IntentSender);
            _log?.Invoke($"handed AetherNetService ({packageBytes.Length} bytes) to the phone's installer, session {id}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or Java.Lang.Exception)
        {
            _log?.Invoke($"could not hand AetherNetService to the phone's installer: {ex.Message}");
            session?.Abandon();
            return false;
        }
        finally
        {
            session?.Close();
        }
    }

    /// <summary>The phone's answers about one install.</summary>
    private sealed class Answer(AndroidNodePackageInstaller owner, string action) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context is null || intent?.Action != action) return;

            var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
            if (status == PackageInstallStatus.PendingUserAction)
            {
                // The phone's own "install this app?" screen. Shown from here, while the asking app is in front.
                var confirm = Confirmation(intent);
                if (confirm is null)
                {
                    owner._log?.Invoke("the phone asked for confirmation but sent no screen to show");
                    return;
                }

                confirm.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(confirm);
                return;
            }

            owner._log?.Invoke(status == PackageInstallStatus.Success
                ? "AetherNetService installed"
                : $"AetherNetService not installed: {status} {intent.GetStringExtra(PackageInstaller.ExtraStatusMessage)}");
            try { context.UnregisterReceiver(this); } catch (Java.Lang.IllegalArgumentException) { /* already gone */ }
        }

        private static Intent? Confirmation(Intent intent)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                return intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent))) as Intent;
#pragma warning disable CA1422, CS0618 // the only way before Android 13
            return intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
#pragma warning restore CA1422, CS0618
        }
    }
}
#endif
