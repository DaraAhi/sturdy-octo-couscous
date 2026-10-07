using System.Text;

namespace Ritplanning.Core;

/// <summary>
/// Kernlogica van de ritplanningmodule: wie mag welke ritten zien/wijzigen (RBAC),
/// auditlog en bewaartermijn. Opslag is in-memory (PoC); in productie komt hier de SQL-database.
/// </summary>
public sealed class RitService
{
    public const int BewaartermijnMaanden = 24;

    private readonly object _lock = new();
    private readonly List<Rit> _ritten;
    private readonly List<AuditEntry> _audit = new();
    private readonly INotifier _notifier;
    private readonly IDocumentStore _docs;
    private readonly TimeProvider _time;

    public RitService(IEnumerable<Rit> seed, INotifier notifier, IDocumentStore docs, TimeProvider time)
    {
        _ritten = seed.ToList();
        _notifier = notifier;
        _docs = docs;
        _time = time;
    }

    private bool ZichtbaarVoor(Gebruiker g, Rit r)
    {
        if (g.Is(Rollen.ITBeheerder)) return true;
        if (g.Is(Rollen.Planner)) return g.Regio is null || r.Regio == g.Regio;
        if (g.Is(Rollen.Chauffeur)) return r.ChauffeurId.Equals(g.Id, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public IReadOnlyList<Rit> Lijst(Gebruiker g)
    {
        lock (_lock)
        {
            Log(g, "lijst-ritten", null);
            return _ritten.Where(r => ZichtbaarVoor(g, r)).OrderBy(r => r.Vertrek).ToList();
        }
    }

    /// <summary>Planner (eigen regio) wijst een rit toe aan een chauffeur; Teams krijgt een melding.</summary>
    public async Task<(Uitkomst, Rit?)> WijsToeAsync(Gebruiker g, int id, string chauffeurId, DateTimeOffset vertrek, CancellationToken ct = default)
    {
        Rit updated;
        lock (_lock)
        {
            var i = _ritten.FindIndex(r => r.Id == id);
            if (i < 0) return (Uitkomst.NietGevonden, null);
            var rit = _ritten[i];
            if (!g.Is(Rollen.Planner) || !ZichtbaarVoor(g, rit))
            {
                Log(g, "toewijzing-geweigerd", id);
                return (Uitkomst.Verboden, null);
            }
            updated = rit with { ChauffeurId = chauffeurId, Vertrek = vertrek };
            _ritten[i] = updated;
            Log(g, "toewijzing", id);
        }
        await _notifier.SendAsync(
            $"Rit {updated.Id} ({updated.Klant}, {updated.Regio}) is toegewezen aan {updated.ChauffeurId}; vertrek {updated.Vertrek:dd-MM-yyyy HH:mm}.", ct);
        return (Uitkomst.Ok, updated);
    }

    /// <summary>Chauffeur bevestigt een eigen rit en levert het ondertekende vrachtdocument aan.</summary>
    public async Task<(Uitkomst, Rit?)> BevestigAsync(Gebruiker g, int id, string bestandsnaam, Stream document, CancellationToken ct = default)
    {
        Rit rit;
        lock (_lock)
        {
            var found = _ritten.FirstOrDefault(r => r.Id == id);
            if (found is null) return (Uitkomst.NietGevonden, null);
            if (!g.Is(Rollen.Chauffeur) || !ZichtbaarVoor(g, found))
            {
                Log(g, "bevestiging-geweigerd", id);
                return (Uitkomst.Verboden, null);
            }
            rit = found;
        }
        var now = _time.GetUtcNow();
        var url = await _docs.SaveAsync(rit.Id, rit.Regio, now, bestandsnaam, document, ct);
        Rit updated;
        lock (_lock)
        {
            var i = _ritten.FindIndex(r => r.Id == id);
            if (i < 0) return (Uitkomst.NietGevonden, null);
            updated = _ritten[i] with { Bevestigd = now, DocumentUrl = url };
            _ritten[i] = updated;
            Log(g, "levering-bevestigd", id);
        }
        return (Uitkomst.Ok, updated);
    }

    public IReadOnlyList<AuditEntry>? AuditLog(Gebruiker g)
    {
        if (!g.Is(Rollen.ITBeheerder)) return null;
        lock (_lock) return _audit.TakeLast(200).ToList();
    }

    /// <summary>Verwijdert ritten ouder dan de bewaartermijn (24 maanden). Geeft het aantal verwijderde ritten terug.</summary>
    public int Opschonen()
    {
        var grens = _time.GetUtcNow().AddMonths(-BewaartermijnMaanden);
        lock (_lock)
        {
            var n = _ritten.RemoveAll(r => r.Aangemaakt < grens);
            if (n > 0) _audit.Add(new AuditEntry(_time.GetUtcNow(), "systeem", $"bewaartermijn-opschoning ({n})", null));
            return n;
        }
    }

    public string NaarErpCsv()
    {
        lock (_lock) return ErpExport.NaarCsv(_ritten);
    }

    private void Log(Gebruiker g, string actie, int? ritId) =>
        _audit.Add(new AuditEntry(_time.GetUtcNow(), g.Id, actie, ritId));
}

public static class ErpExport
{
    public static string NaarCsv(IEnumerable<Rit> ritten)
    {
        var sb = new StringBuilder("RitId;Regio;Klant;Vertrek;Bevestigd\n");
        foreach (var r in ritten.OrderBy(r => r.Id))
            sb.Append(r.Id).Append(';').Append(Veld(r.Regio)).Append(';').Append(Veld(r.Klant)).Append(';')
              .Append(r.Vertrek.ToString("yyyy-MM-dd'T'HH:mm")).Append(';')
              .Append(r.Bevestigd?.ToString("yyyy-MM-dd'T'HH:mm") ?? "").Append('\n');
        return sb.ToString();
    }

    private static string Veld(string s) =>
        s.Contains(';') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

public static class SeedData
{
    public static List<Rit> Maak(IReadOnlyList<string> chauffeurs, DateTimeOffset nu)
    {
        string[] regios = { "Nuenen", "Eindhoven", "Roermond" };
        string[] klanten = { "Bouwgroep Peel", "Vers Zuid", "Houthandel Kempen", "Foodlog Brabant", "Bouwservice Limburg", "Fresh Direct" };
        var lijst = new List<Rit>();
        for (var i = 1; i <= 12; i++)
        {
            lijst.Add(new Rit
            {
                Id = i,
                Regio = regios[(i - 1) % regios.Length],
                ChauffeurId = chauffeurs[(i - 1) % chauffeurs.Count],
                Klant = klanten[(i - 1) % klanten.Length],
                Adres = $"Industrieweg {i * 3}, {regios[(i - 1) % regios.Length]}",
                Vertrek = nu.Date.AddHours(6 + i),
                Aangemaakt = nu.AddDays(-i),
            });
        }
        return lijst;
    }
}
