using Ritplanning.Core;

// Eenvoudige testrunner (geen externe packages nodig). Exitcode 1 bij een mislukte test,
// zodat de CI/CD-pipeline de uitrol blokkeert.
var failures = 0;
void Test(string naam, Func<Task> body)
{
    try { body().GetAwaiter().GetResult(); Console.WriteLine($"  OK    {naam}"); }
    catch (Exception e) { failures++; Console.WriteLine($"  FAIL  {naam}: {e.Message}"); }
}
void Assert(bool voorwaarde, string melding) { if (!voorwaarde) throw new Exception(melding); }

var nu = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

Gebruiker Maak(string id, string rol, string? regio = null) =>
    new(id, id, new HashSet<string> { rol }, regio);

(RitService, FakeNotifier, FakeDocs, FakeTime) Bouw()
{
    var n = new FakeNotifier(); var d = new FakeDocs(); var t = new FakeTime(nu);
    var seed = SeedData.Maak(new[] { "chauffeur1@draloop.test", "chauffeur2@draloop.test" }, nu);
    return (new RitService(seed, n, d, t), n, d, t);
}

Console.WriteLine("Ritplanning unit tests");

Test("IT-beheerder ziet alle ritten", () =>
{
    var (s, _, _, _) = Bouw();
    Assert(s.Lijst(Maak("it@draloop.test", Rollen.ITBeheerder)).Count == 12, "verwacht 12 ritten");
    return Task.CompletedTask;
});

Test("Planner ziet alleen ritten van de eigen regio", () =>
{
    var (s, _, _, _) = Bouw();
    var lijst = s.Lijst(Maak("p@draloop.test", Rollen.Planner, "Eindhoven"));
    Assert(lijst.Count == 4 && lijst.All(r => r.Regio == "Eindhoven"), "alleen Eindhoven verwacht");
    return Task.CompletedTask;
});

Test("Chauffeur ziet alleen eigen ritten", () =>
{
    var (s, _, _, _) = Bouw();
    var lijst = s.Lijst(Maak("chauffeur1@draloop.test", Rollen.Chauffeur));
    Assert(lijst.Count == 6 && lijst.All(r => r.ChauffeurId == "chauffeur1@draloop.test"), "alleen eigen ritten verwacht");
    return Task.CompletedTask;
});

Test("Gebruiker zonder rol ziet niets", () =>
{
    var (s, _, _, _) = Bouw();
    Assert(s.Lijst(new Gebruiker("x", "x", new HashSet<string>(), null)).Count == 0, "geen ritten verwacht");
    return Task.CompletedTask;
});

Test("Planner kan rit in eigen regio toewijzen en Teams-melding wordt verstuurd", async () =>
{
    var (s, n, _, _) = Bouw();
    var (u, rit) = await s.WijsToeAsync(Maak("p", Rollen.Planner, "Nuenen"), 1, "chauffeur2@draloop.test", nu.AddHours(2));
    Assert(u == Uitkomst.Ok && rit!.ChauffeurId == "chauffeur2@draloop.test", "toewijzing had moeten slagen");
    Assert(n.Berichten.Count == 1 && n.Berichten[0].Contains("Rit 1"), "Teams-melding verwacht");
});

Test("Planner mag geen rit van een andere regio wijzigen", async () =>
{
    var (s, n, _, _) = Bouw();
    var (u, _) = await s.WijsToeAsync(Maak("p", Rollen.Planner, "Roermond"), 1, "x", nu);
    Assert(u == Uitkomst.Verboden && n.Berichten.Count == 0, "verboden verwacht, geen melding");
});

Test("Chauffeur kan geen toewijzing wijzigen", async () =>
{
    var (s, _, _, _) = Bouw();
    var (u, _) = await s.WijsToeAsync(Maak("chauffeur1@draloop.test", Rollen.Chauffeur), 1, "x", nu);
    Assert(u == Uitkomst.Verboden, "verboden verwacht");
});

Test("Chauffeur bevestigt eigen rit en document wordt opgeslagen", async () =>
{
    var (s, _, d, _) = Bouw();
    var (u, rit) = await s.BevestigAsync(Maak("chauffeur1@draloop.test", Rollen.Chauffeur), 1, "cmr.pdf", new MemoryStream(new byte[] { 1, 2, 3 }));
    Assert(u == Uitkomst.Ok && rit!.Bevestigd is not null && rit.DocumentUrl is not null, "bevestiging verwacht");
    Assert(d.Opgeslagen == 1, "document had opgeslagen moeten zijn");
});

Test("Chauffeur kan rit van collega niet bevestigen", async () =>
{
    var (s, _, d, _) = Bouw();
    var (u, _) = await s.BevestigAsync(Maak("chauffeur1@draloop.test", Rollen.Chauffeur), 2, "cmr.pdf", new MemoryStream());
    Assert(u == Uitkomst.Verboden && d.Opgeslagen == 0, "verboden verwacht, niets opgeslagen");
});

Test("Auditlog alleen voor IT-beheerder", () =>
{
    var (s, _, _, _) = Bouw();
    s.Lijst(Maak("p", Rollen.Planner, "Nuenen"));
    Assert(s.AuditLog(Maak("p", Rollen.Planner, "Nuenen")) is null, "planner mag auditlog niet zien");
    Assert(s.AuditLog(Maak("it", Rollen.ITBeheerder))!.Count >= 1, "IT ziet auditlog");
    return Task.CompletedTask;
});

Test("Bewaartermijn: ritten ouder dan 24 maanden worden verwijderd", () =>
{
    var (s, _, _, t) = Bouw();
    t.Zet(nu.AddMonths(24).AddDays(8));   // alle seed-ritten (1-12 dagen oud) zijn dan >24 mnd
    var n = s.Opschonen();
    Assert(n == 12 && s.Lijst(Maak("it", Rollen.ITBeheerder)).Count == 0, "alle 12 ritten verwacht verwijderd");
    return Task.CompletedTask;
});

Test("Bewaartermijn: recente ritten blijven bestaan", () =>
{
    var (s, _, _, _) = Bouw();
    Assert(s.Opschonen() == 0, "niets verwijderd verwacht");
    return Task.CompletedTask;
});

Test("ERP-mock CSV escapet scheidingstekens", () =>
{
    var csv = ErpExport.NaarCsv(new[] { new Rit { Id = 1, Regio = "A;B", Klant = "Say \"hi\"", Vertrek = nu } });
    Assert(csv.Contains("\"A;B\"") && csv.Contains("\"Say \"\"hi\"\"\""), "escaping fout: " + csv);
    return Task.CompletedTask;
});

Console.WriteLine(failures == 0 ? "Alle tests geslaagd." : $"{failures} test(s) mislukt.");
return failures == 0 ? 0 : 1;

sealed class FakeNotifier : INotifier
{
    public List<string> Berichten { get; } = new();
    public Task SendAsync(string tekst, CancellationToken ct = default) { Berichten.Add(tekst); return Task.CompletedTask; }
}
sealed class FakeDocs : IDocumentStore
{
    public int Opgeslagen { get; private set; }
    public Task<string> SaveAsync(int ritId, string regio, DateTimeOffset datum, string bestandsnaam, Stream inhoud, CancellationToken ct = default)
    { Opgeslagen++; return Task.FromResult($"fake://{regio}/{ritId}/{bestandsnaam}"); }
}
sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _nu;
    public FakeTime(DateTimeOffset nu) => _nu = nu;
    public void Zet(DateTimeOffset nu) => _nu = nu;
    public override DateTimeOffset GetUtcNow() => _nu;
}
