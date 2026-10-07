using Ritplanning.Core;
using Ritplanning.Web;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ManagedIdentityTokenProvider>(sp =>
    new ManagedIdentityTokenProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient()));

builder.Services.AddSingleton<INotifier>(sp =>
{
    var url = cfg["TEAMS_WEBHOOK_URL"];
    return string.IsNullOrWhiteSpace(url)
        ? new ConsoleNotifier(sp.GetRequiredService<ILogger<ConsoleNotifier>>())
        : new TeamsWebhookNotifier(sp.GetRequiredService<IHttpClientFactory>().CreateClient(), url,
            sp.GetRequiredService<ILogger<TeamsWebhookNotifier>>());
});

builder.Services.AddSingleton<IDocumentStore>(sp =>
{
    var drive = cfg["GRAPH_DRIVE_ID"];
    return string.IsNullOrWhiteSpace(drive)
        ? new LocalFolderStore(cfg["DOCS_PATH"] ?? Path.Combine(Path.GetTempPath(), "ritplanning"))
        : new GraphSharePointStore(sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
            sp.GetRequiredService<ManagedIdentityTokenProvider>(), drive);
});

builder.Services.AddSingleton(sp =>
{
    var chauffeurs = (cfg["DEMO_CHAUFFEURS"] ?? "chauffeur1@draloop.test,chauffeur2@draloop.test")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(c => c.ToLowerInvariant()).ToList();
    var time = sp.GetRequiredService<TimeProvider>();
    return new RitService(SeedData.Maak(chauffeurs, time.GetUtcNow()),
        sp.GetRequiredService<INotifier>(), sp.GetRequiredService<IDocumentStore>(), time);
});
builder.Services.AddHostedService<OpschoonService>();

var app = builder.Build();
var versie = cfg["APP_VERSION"] ?? "dev";
var maxUpload = 10 * 1024 * 1024;

app.UseDefaultFiles();
app.UseStaticFiles();

// Health endpoint voor Container Apps probes en voor de pipeline (geen authenticatie nodig).
// BREAK_HEALTH=true laat bewust falen: zo is de automatische rollback aantoonbaar te testen (T7).
app.MapGet("/health", () =>
    cfg.GetValue<bool>("BREAK_HEALTH")
        ? Results.Json(new { status = "fout", versie }, statusCode: 503)
        : Results.Json(new { status = "ok", versie }));

// Alles onder /api en /mock vereist een geauthenticeerde gebruiker uit Entra ID.
var beveiligd = app.MapGroup("")
    .AddEndpointFilter(async (ctx, next) =>
    {
        var http = ctx.HttpContext;
        var g = EasyAuth.Lees(http.Request, cfg, http.RequestServices.GetRequiredService<IHostEnvironment>());
        if (g is null) return Results.Unauthorized();
        // Eenvoudige CSRF-bescherming voor wijzigende verzoeken (cookie-sessie van de ingebouwde login).
        if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Headers["X-Requested-With"] != "ritplanning")
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        http.Items["gebruiker"] = g;
        return await next(ctx);
    });

Gebruiker Wie(HttpContext c) => (Gebruiker)c.Items["gebruiker"]!;

beveiligd.MapGet("/api/me", (HttpContext c) =>
{
    var g = Wie(c);
    return Results.Json(new { g.Id, g.Naam, rollen = g.Rollen, g.Regio, versie });
});

beveiligd.MapGet("/api/ritten", (HttpContext c, RitService s) => Results.Json(s.Lijst(Wie(c))));

beveiligd.MapPut("/api/ritten/{id:int}/toewijzing", async (int id, ToewijzingVerzoek body, HttpContext c, RitService s) =>
{
    if (string.IsNullOrWhiteSpace(body.ChauffeurId)) return Results.BadRequest(new { fout = "chauffeurId is verplicht" });
    var (u, rit) = await s.WijsToeAsync(Wie(c), id, body.ChauffeurId.Trim().ToLowerInvariant(), body.Vertrek, c.RequestAborted);
    return Vertaal(u, rit);
});

// Body = bestandsinhoud; bestandsnaam in query (?bestandsnaam=cmr.pdf).
beveiligd.MapPost("/api/ritten/{id:int}/bevestig", async (int id, string? bestandsnaam, HttpContext c, RitService s) =>
{
    if (c.Request.ContentLength is null or 0 or > 10 * 1024 * 1024)
        return Results.BadRequest(new { fout = $"Document verplicht en maximaal {maxUpload / 1024 / 1024} MB" });
    var (u, rit) = await s.BevestigAsync(Wie(c), id, bestandsnaam ?? "document.bin", c.Request.Body, c.RequestAborted);
    return Vertaal(u, rit);
});

beveiligd.MapGet("/api/beheer/audit", (HttpContext c, RitService s) =>
    s.AuditLog(Wie(c)) is { } log ? Results.Json(log) : Results.StatusCode(StatusCodes.Status403Forbidden));

// Nagebootste ERP-koppeling: levert dezelfde CSV-structuur als de nachtelijke export van het echte ERP-pakket.
if (cfg.GetValue("ERP_MOCK", true))
    beveiligd.MapGet("/mock/erp/export", (HttpContext c, RitService s) =>
        Wie(c).Is(Rollen.ITBeheerder) ? Results.Text(s.NaarErpCsv(), "text/csv") : Results.StatusCode(StatusCodes.Status403Forbidden));

app.Run();

static IResult Vertaal(Uitkomst u, Rit? rit) => u switch
{
    Uitkomst.Ok => Results.Json(rit),
    Uitkomst.NietGevonden => Results.NotFound(),
    _ => Results.StatusCode(StatusCodes.Status403Forbidden),
};

public sealed record ToewijzingVerzoek(string ChauffeurId, DateTimeOffset Vertrek);
