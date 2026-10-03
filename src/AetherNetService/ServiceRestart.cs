// SPDX-License-Identifier: MIT
namespace AetherNetService;

/// <summary>
/// How AetherNetService applies a change to its radios: the way a cable is re-plugged. A radio once stopped cannot be
/// started again (its <c>Stop</c> disposes it), so the setting is written, this process ends, and the next one comes up
/// with the radios as asked — started at once for the app still connected to it: a bind on a phone, the app's client
/// starting it again when the pipe closes on a computer. Connected apps reconnect by themselves, as after an update.
/// </summary>
internal static class ServiceRestart
{
    /// <summary>Long enough for the reply to reach the app that asked before the process goes.</summary>
    private static readonly TimeSpan ReplyFirst = TimeSpan.FromMilliseconds(500);

    public static void AfterTheReply() => _ = Task.Run(async () =>
    {
        await Task.Delay(ReplyFirst).ConfigureAwait(false);
#if ANDROID
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
#else
        Environment.Exit(0);
#endif
    });
}
