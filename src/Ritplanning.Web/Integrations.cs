using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ritplanning.Core;

namespace Ritplanning.Web;

/// <summary>Haalt een Graph-token op via de managed identity van de Container App (geen wachtwoorden of secrets).</summary>
public sealed class ManagedIdentityTokenProvider
{
    private readonly HttpClient _http;
    private string? _token;
    private DateTimeOffset _verloopt;
    private readonly SemaphoreSlim _slot = new(1, 1);

    public ManagedIdentityTokenProvider(HttpClient http) => _http = http;

    public async Task<string> GetAsync(string resource = "https://graph.microsoft.com", CancellationToken ct = default)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _verloopt.AddMinutes(-5)) return _token;
        await _slot.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _verloopt.AddMinutes(-5)) return _token;
            var endpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT")
                ?? throw new InvalidOperationException("IDENTITY_ENDPOINT ontbreekt: managed identity is niet ingeschakeld.");
            var header = Environment.GetEnvironmentVariable("IDENTITY_HEADER")
                ?? throw new InvalidOperationException("IDENTITY_HEADER ontbreekt.");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?resource={Uri.EscapeDataString(resource)}&api-version=2019-08-01");
            req.Headers.Add("X-IDENTITY-HEADER", header);
            using var res = await _http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            _token = doc.RootElement.GetProperty("access_token").GetString()!;
            var exp = doc.RootElement.GetProperty("expires_on").GetString()!;
            _verloopt = long.TryParse(exp, out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow.AddMinutes(30);
            return _token;
        }
        finally { _slot.Release(); }
    }
}

/// <summary>Stuurt meldingen naar een Teams-kanaal via een Teams Workflows-webhook (URL staat als secret in Key Vault).</summary>
public sealed class TeamsWebhookNotifier : INotifier
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly ILogger<TeamsWebhookNotifier> _log;

    public TeamsWebhookNotifier(HttpClient http, string url, ILogger<TeamsWebhookNotifier> log)
    { _http = http; _url = url; _log = log; }

    public async Task SendAsync(string tekst, CancellationToken ct = default)
    {
        var card = new Dictionary<string, object>
        {
            ["type"] = "message",
            ["attachments"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["content"] = new Dictionary<string, object>
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = new object[]
                        {
                            new Dictionary<string, object> { ["type"] = "TextBlock", ["weight"] = "Bolder", ["text"] = "Ritplanning" },
                            new Dictionary<string, object> { ["type"] = "TextBlock", ["wrap"] = true, ["text"] = tekst },
                        },
                    },
                },
            },
        };
        try
        {
            using var res = await _http.PostAsync(_url,
                new StringContent(JsonSerializer.Serialize(card), Encoding.UTF8, "application/json"), ct);
            if (!res.IsSuccessStatusCode) _log.LogWarning("Teams-melding mislukt: HTTP {Status}", (int)res.StatusCode);
        }
        catch (HttpRequestException e)
        {
            // Een mislukte melding mag de planning niet blokkeren.
            _log.LogWarning(e, "Teams-melding mislukt");
        }
    }
}

/// <summary>Slaat vrachtdocumenten op in een SharePoint-documentbibliotheek via Microsoft Graph (Sites.Selected, alleen die ene site).</summary>
public sealed class GraphSharePointStore : IDocumentStore
{
    private readonly HttpClient _http;
    private readonly ManagedIdentityTokenProvider _tokens;
    private readonly string _driveId;

    public GraphSharePointStore(HttpClient http, ManagedIdentityTokenProvider tokens, string driveId)
    { _http = http; _tokens = tokens; _driveId = driveId; }

    public async Task<string> SaveAsync(int ritId, string regio, DateTimeOffset datum, string bestandsnaam, Stream inhoud, CancellationToken ct = default)
    {
        var naam = $"rit-{ritId}-{Veilig(bestandsnaam)}";
        var pad = string.Join('/', new[] { "Vrachtdocumenten", Veilig(regio), datum.ToString("yyyy-MM-dd"), naam }.Select(Uri.EscapeDataString));
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"https://graph.microsoft.com/v1.0/drives/{_driveId}/root:/{pad}:/content");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAsync(ct: ct));
        req.Content = new StreamContent(inhoud);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("webUrl").GetString()!;
    }

    private static string Veilig(string s)
    {
        var n = Path.GetFileName(s);
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '"', '*', ':', '<', '>', '?', '/', '\\', '|', '#', '%' }))
            n = n.Replace(c, '_');
        return n.Length == 0 ? "document" : n;
    }
}

/// <summary>Lokale vervanger zodat de module zonder Microsoft 365 kan draaien en testen.</summary>
public sealed class ConsoleNotifier : INotifier
{
    private readonly ILogger<ConsoleNotifier> _log;
    public ConsoleNotifier(ILogger<ConsoleNotifier> log) => _log = log;
    public Task SendAsync(string tekst, CancellationToken ct = default)
    { _log.LogInformation("[Teams-melding (lokaal)] {Tekst}", tekst); return Task.CompletedTask; }
}

public sealed class LocalFolderStore : IDocumentStore
{
    private readonly string _root;
    public LocalFolderStore(string root) => _root = root;

    public async Task<string> SaveAsync(int ritId, string regio, DateTimeOffset datum, string bestandsnaam, Stream inhoud, CancellationToken ct = default)
    {
        var dir = Path.Combine(_root, "Vrachtdocumenten", regio, datum.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        var pad = Path.Combine(dir, $"rit-{ritId}-{Path.GetFileName(bestandsnaam)}");
        await using var fs = File.Create(pad);
        await inhoud.CopyToAsync(fs, ct);
        return "file://" + pad;
    }
}

/// <summary>Voert dagelijks de bewaartermijn uit (AVG: niet langer bewaren dan nodig).</summary>
public sealed class OpschoonService : BackgroundService
{
    private readonly RitService _ritten;
    private readonly ILogger<OpschoonService> _log;
    public OpschoonService(RitService ritten, ILogger<OpschoonService> log) { _ritten = ritten; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            var n = _ritten.Opschonen();
            if (n > 0) _log.LogInformation("Bewaartermijn: {N} ritten verwijderd", n);
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
