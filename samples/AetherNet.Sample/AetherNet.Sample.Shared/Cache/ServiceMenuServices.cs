// The app's side of AetherNetService's menu (NodeOp): what the app's container hands its pages, made with the classes.
using Microsoft.Extensions.DependencyInjection;

namespace AetherNet.Sample.Shared.Cache;

public static class ServiceMenuServices
{
    /// <summary>The menu, and every class a page is handed that answers from it, as the pages ask for them.</summary>
    public static IServiceCollection AddServiceMenu(this IServiceCollection services)
    {
        services.AddSingleton<ServiceMenu>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.AetherDemoService>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Data.AetherStore>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.AppHandout>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.AttachmentService>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.AwareService>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.CallService>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.Cast.CastService>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.ChatService>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.ContactService>();
        services.AddSingleton<global::AetherNet.Browser.Deck>();
        services.AddSingleton<global::AetherNet.Browser.Decks>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.GroupCallService>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.IAppShareService, global::AetherNet.Sample.Shared.Services.AppShareServiceFromService>();
        services.AddSingleton<global::AetherNet.Mesh.IIdentityService, global::AetherNet.Mesh.IdentityServiceFromService>();
        services.AddSingleton<global::AetherNet.Mesh.IRadioMesh, global::AetherNet.Mesh.RadioMeshFromService>();
        services.AddSingleton<global::AetherNet.Mesh.IRadioSetup, global::AetherNet.Mesh.RadioSetupFromService>();
        services.AddSingleton<global::AetherNet.Mesh.IWifiDirectGroup, global::AetherNet.Mesh.WifiDirectGroupFromService>();
        services.AddSingleton<global::AetherNet.Browser.MeshWebService>();
        services.AddSingleton<global::AetherNet.Browser.MyPages>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.PanicWipeService>();
        services.AddSingleton<global::AetherNet.Mesh.ProxyDirectory>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.QuietHelpService>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.SosService>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.Cast.UpnpRendererService>();
        services.AddSingleton<global::AetherNet.Browser.Wanted>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.WarmUpService>();
        services.AddSingleton<global::AetherNet.Sample.Shared.Services.WatchService>();
        return services;
    }
}
