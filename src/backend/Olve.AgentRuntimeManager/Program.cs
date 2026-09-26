using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Configuration;
using Olve.AgentRuntimeManager.Events;
using Olve.AgentRuntimeManager.Health;
using Olve.AgentRuntimeManager.Sessions;
using Olve.Utilities.AsyncOnStartup;

var builder = WebApplication.CreateSlimBuilder(args);

builder.ConfigureHost(args);
builder.ConfigureJson();
builder.ConfigureAuthentication();
builder.ConfigureTelemetry();
builder.Services.AddEventServices(builder.Configuration);
builder.Services.AddSessionServices(builder.Configuration);
builder.Services.AddSingleton<IAuthConfigGetHandler, GetAuthConfigHandler>();

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

// The JSON API (under /api/, so the SPA can own the site root) is generated from the contract
// (src/spec/main.tsp → artifacts/generated/backend). Every operation needs an authenticated
// user (the fallback policy) unless opted out here, matching the contract's @useAuth(NoAuth).
var api = app.UseArmApi();
api.AuthConfigGet.AllowAnonymous();

// SPA client-side routing: any unmatched non-API GET returns index.html so deep links work.
app.MapFallbackToFile("index.html").AllowAnonymous();

// Start the host, then run one-shot startup tasks (IAsyncOnStartup), then block until shutdown.
await app.StartAsync();
await app.Services.RunAsyncOnStartup();
await app.WaitForShutdownAsync();

public partial class Program;
