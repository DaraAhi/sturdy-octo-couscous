using System.Text;
using System.Text.Json;
using Ritplanning.Core;

namespace Ritplanning.Web;

/// <summary>
/// Leest de ingelogde gebruiker uit de header X-MS-CLIENT-PRINCIPAL die Azure Container Apps
/// (ingebouwde authenticatie met Microsoft Entra ID) na een geslaagde login toevoegt.
/// Het platform verwijdert deze header uit inkomende clientverzoeken, dus hij kan niet worden vervalst
/// zolang "Authentication" aan staat met "Require authentication".
/// </summary>
public static class EasyAuth
{
    public static Gebruiker? Lees(HttpRequest req, IConfiguration cfg, IHostEnvironment env)
    {
        // Alleen voor lokaal ontwikkelen/testen; nooit in productie.
        if (env.IsDevelopment() && cfg.GetValue<bool>("Auth:DevHeader")
            && req.Headers.TryGetValue("X-Dev-User", out var dev))
        {
            // formaat: id|naam|rol1,rol2|regio
            var d = dev.ToString().Split('|');
            if (d.Length >= 3)
                return new Gebruiker(d[0].ToLowerInvariant(), d[1],
                    d[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(),
                    d.Length > 3 && d[3].Length > 0 ? d[3] : null);
        }

        if (!req.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var raw) || raw.Count == 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(raw.ToString()));
            var root = doc.RootElement;
            var roleTyp = root.TryGetProperty("role_typ", out var rt) ? rt.GetString() : null;

            string? Claim(string typ) => root.GetProperty("claims").EnumerateArray()
                .Where(c => c.GetProperty("typ").GetString() == typ)
                .Select(c => c.GetProperty("val").GetString()).FirstOrDefault();

            var rollen = root.GetProperty("claims").EnumerateArray()
                .Where(c =>
                {
                    var t = c.GetProperty("typ").GetString();
                    return t == "roles" || t == roleTyp || t == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
                })
                .Select(c => c.GetProperty("val").GetString()!)
                .ToHashSet();

            var id = (Claim("preferred_username")
                      ?? (req.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL-NAME", out var n) ? n.ToString() : null)
                      ?? Claim("oid"))?.ToLowerInvariant();
            if (id is null) return null;

            var naam = Claim("name") ?? id;
            var regio = Claim("regio") ?? RegioUitConfig(cfg, id);
            return new Gebruiker(id, naam, rollen, regio);
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>REGIO_MAP="planner1@x.nl=Eindhoven;planner2@x.nl=Roermond"</summary>
    private static string? RegioUitConfig(IConfiguration cfg, string id)
    {
        var map = cfg["REGIO_MAP"];
        if (string.IsNullOrWhiteSpace(map)) return null;
        foreach (var paar in map.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var d = paar.Split('=', 2);
            if (d.Length == 2 && d[0].Trim().Equals(id, StringComparison.OrdinalIgnoreCase)) return d[1].Trim();
        }
        return null;
    }
}
