using AetherNet.Content;
using AetherNet.Content.Sqlite;
using AetherNet.Sample.Web.Components;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add device-specific services used by the AetherNet.Sample.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Durable state for this host. The Web head is a demo surface, so it keeps its database beside the
// app rather than in a phone's private storage.
var dataDir = Path.Combine(AppContext.BaseDirectory, "aether-data");
Directory.CreateDirectory(dataDir);
builder.Services.AddSingleton(_ => new AetherStore(Path.Combine(dataDir, "aether.db")));
builder.Services.AddSingleton<IContentStore>(_ => new SqliteContentStore(Path.Combine(dataDir, "content.db")));
builder.Services.AddSingleton<ISecretVault>(_ => new FileSecretVault(Path.Combine(dataDir, "vault")));

// This device's node identity. The app asks for it rather than minting one — the identity belongs to
// the device, not to whichever app happened to run first.
builder.Services.AddSingleton<AetherNet.Identity.INodeIdentityStore>(sp =>
    new VaultNodeIdentityStore(sp.GetRequiredService<ISecretVault>()));
builder.Services.AddSingleton<AetherNet.Identity.INodeIdentity>(sp =>
    new AetherNet.Identity.NodeIdentity(sp.GetRequiredService<AetherNet.Identity.INodeIdentityStore>()));
builder.Services.AddSingleton<IIdentityService, IdentityService>();

// The people this device knows, and the add/be-added handshake.
builder.Services.AddSingleton<ContactService>();

// Same real messaging stack as the phone. This host has no radio, so messages stay pending —
// honestly queued rather than pretending to send.
// Sessions live in the device database so a conversation survives a restart. Without it both
// sides rebuild as X3DH initiator and diverge into two ratchets for one pair.
builder.Services.AddSingleton<AetherNet.Security.Services.ISignalSessionBlobStore>(sp =>
    new StoredSignalSessions(sp.GetRequiredService<AetherStore>()));
builder.Services.AddSingleton<AetherNet.Security.Services.ISignalProtocolService>(sp =>
    new AetherNet.Security.Services.SignalProtocolService(
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AetherNet.Security.Services.SignalProtocolService>>(),
        sp.GetRequiredService<AetherNet.Security.Services.ISignalSessionBlobStore>()));
builder.Services.AddSingleton<AetherNet.PreKeys.IPreKeyExchangeService>(sp =>
    new AetherNet.PreKeys.PreKeyExchangeService(
        new RadioMeshSender(sp.GetRequiredService<IIdentityService>().AetherTag,
            sp.GetRequiredService<IRadioMesh>())));
builder.Services.AddSingleton<ChatService>();

// Radios are physical; this host has none, so setup says so honestly.
builder.Services.AddSingleton<IRadioSetup, NullRadioSetup>();

// So is a microphone. The call service exists here so the UI compiles and can explain itself, but it
// will always say a call is impossible rather than pretending to place one.
builder.Services.AddSingleton<IAudioIo, NullAudioIo>();
builder.Services.AddSingleton<IWifiDirectGroup, NullWifiDirectGroup>();
builder.Services.AddSingleton<WifiDirectBroker>();
builder.Services.AddSingleton<CallService>();


// The live in-process AetherNet mesh that the demo UI drives. Singleton on the server: the
// InProcess transport uses a process-wide registry, so one shared mesh avoids UHID collisions
// between Blazor's prerender and interactive renders (and between browser circuits).
builder.Services.AddSingleton<AetherDemoService>();

// The mesh-web: signed, content-addressed pages served at aether:// addresses. Singleton so
// prerender and interactive renders share one node (the InProcess registry is process-wide).
builder.Services.AddSingleton<MeshWebService>();

// Radios are physical; the Web host has none.
builder.Services.AddSingleton<IRadioMesh, NullRadioMesh>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(
        typeof(AetherNet.Sample.Shared._Imports).Assembly);

app.Run();
