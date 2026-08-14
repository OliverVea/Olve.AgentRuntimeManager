using Olve.AgentRuntimeManager.Configuration;
using Olve.AgentRuntimeManager.Health;
using Olve.AgentRuntimeManager.Messages;
using Olve.Utilities.AsyncOnStartup;

var builder = WebApplication.CreateSlimBuilder(args);

builder.ConfigureHost(args);
builder.ConfigureJson();
builder.ConfigureAuthentication();
builder.ConfigureTelemetry();
builder.Services.AddMessageServices(builder.Configuration);

var app = builder.Build();

// Serve the SPA (frontend/dist, copied into wwwroot by the Dockerfile) at the site root.
// Static assets and the index fallback are anonymous — the RequireAuthenticatedUser fallback
// policy would otherwise 401 them. In local dev the SPA is served by Vite (`npm run dev`),
// so wwwroot is only populated in the container and this no-ops when running `dotnet run`.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapJson();
app.MapAuthentication();
app.MapHealthEndpoints();

// The JSON API lives under /api/ so the SPA can own the site root (/, /index.html, assets).
var api = app.MapGroup("/api");
api.MapMessageEndpoints();
api.MapFrontendConfig();

// SPA client-side routing: any unmatched non-API GET returns index.html so deep links work.
app.MapFallbackToFile("index.html").AllowAnonymous();

// Start the host (the persister loads its snapshot here), then run one-shot startup tasks
// against the populated stores, then block until shutdown.
await app.StartAsync();
await app.Services.RunAsyncOnStartup();
await app.WaitForShutdownAsync();

public partial class Program;
