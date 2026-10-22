// The app's side of AetherNetService's menu (NodeOp): what the app's container hands its pages, made with the classes.
using Microsoft.Extensions.DependencyInjection;

namespace Aether.Shared.Cache;

public static class ServiceMenuServices
{
    /// <summary>The menu, and every class a page is handed that answers from it, as the pages ask for them.</summary>
    public static IServiceCollection AddServiceMenu(this IServiceCollection services)
    {
        services.AddSingleton<ServiceMenu>();
        services.AddSingleton<global::Aether.Shared.Services.MeshLabService>();
        services.AddSingleton<global::Aether.Shared.Data.AetherStore>();
        services.AddSingleton<global::Aether.Shared.Services.AppHandout>();
        services.AddSingleton<global::Aether.Shared.Services.AttachmentService>();
        services.AddSingleton<global::Aether.Shared.Services.AwareService>();
        services.AddSingleton<global::Aether.Shared.Services.CallService>();
        services.AddSingleton<global::Aether.Shared.Services.Cast.CastService>();
        services.AddSingleton<global::Aether.Shared.Services.ChatService>();
        services.AddSingleton<global::Aether.Shared.Services.ContactService>();
        services.AddSingleton<global::AetherNet.Browser.Deck>();
        services.AddSingleton<global::AetherNet.Browser.Decks>();
        services.AddSingleton<global::Aether.Shared.Services.GroupCallService>();
        services.AddSingleton<global::Aether.Shared.Services.IAppShareService, global::Aether.Shared.Services.AppShareServiceFromService>();
        services.AddSingleton<global::AetherNet.Mesh.IIdentityService, global::AetherNet.Mesh.IdentityServiceFromService>();
        services.AddSingleton<global::AetherNet.Mesh.IRadioMesh, global::AetherNet.Mesh.RadioMeshFromService>();
        services.AddSingleton<global::AetherNet.Mesh.IRadioSetup, global::AetherNet.Mesh.RadioSetupFromService>();
        services.AddSingleton<global::AetherNet.Mesh.IWifiDirectGroup, global::AetherNet.Mesh.WifiDirectGroupFromService>();
        services.AddSingleton<global::AetherNet.Browser.MeshWebService>();
        services.AddSingleton<global::AetherNet.Browser.MyPages>();
        services.AddSingleton<global::Aether.Shared.Services.PanicWipeService>();
        services.AddSingleton<global::AetherNet.Mesh.ProxyDirectory>();
        services.AddSingleton<global::Aether.Shared.Services.QuietHelpService>();
        services.AddSingleton<global::Aether.Shared.Services.SosService>();
        services.AddSingleton<global::Aether.Shared.Services.Cast.UpnpRendererService>();
        services.AddSingleton<global::AetherNet.Browser.Wanted>();
        services.AddSingleton<global::Aether.Shared.Services.WarmUpService>();
        services.AddSingleton<global::Aether.Shared.Services.WatchService>();
        return services;
    }
}
