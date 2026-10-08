// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class VaultLabDemo : global::System.IDisposable
{
    public const int K = 10;
    public const int M = 4;
    public const int N = K + M;
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public VaultLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewVaultLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetVaultLab, global::AetherNetNodeService.Ipc.NodeOp.EventVaultLab);
    public bool HasBackup => Now.HasBackup;
    public global::AetherNet.Vault.Models.VaultHealth? Health => Now.Health;
    public bool IntegrityOk => Now.IntegrityOk;
    public int OnlineCount => Now.OnlineCount;
    public string? Recovered => Now.Recovered;
    public string Secret
    {
        get => Now.Secret;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetVaultLabSecret, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVaultLab); }
    }

    public async global::System.Threading.Tasks.Task BackupAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.VaultLabBackup, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVaultLab);
    }

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.VaultLabDispose);
    }

    public async global::System.Threading.Tasks.Task LocateShardAsync(int index)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.VaultLabLocateShard, new { index }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVaultLab);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.VaultLabDemo.LogLine> Log()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.VaultLabDemo.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.VaultLabLog, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVaultLab);
        return answer;
    }

    public async global::System.Threading.Tasks.Task ReReplicateAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.VaultLabReReplicate, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVaultLab);
    }

    public async global::System.Threading.Tasks.Task RecoverAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.VaultLabRecover, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVaultLab);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.VaultLabDemo.ShardView> Shards()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.VaultLabDemo.ShardView>>(global::AetherNetNodeService.Ipc.NodeOp.VaultLabShards, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVaultLab);
        return answer;
    }

    public void Start()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.VaultLabStart, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVaultLab);
    }

    public async global::System.Threading.Tasks.Task ToggleShardAsync(int index)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.VaultLabToggleShard, new { index }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVaultLab);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventVaultLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public bool HasBackup { get; init; }
        public global::AetherNet.Vault.Models.VaultHealth? Health { get; init; }
        public bool IntegrityOk { get; init; }
        public int OnlineCount { get; init; }
        public string? Recovered { get; init; }
        public string Secret { get; init; } = default!;
    }

    public record LogLine(string Text, bool Strong);

    public record ShardView(int Index, string Peer, bool IsParity, bool Online, string Hash8);
}
