using AetherNet.Sample.Web.Components;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add device-specific services used by the AetherNet.Sample.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

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
