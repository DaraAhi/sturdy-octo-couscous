namespace Ritplanning.Core;

/// <summary>Waarden van de Entra ID-approllen (zie infra/approles.json).</summary>
public static class Rollen
{
    public const string Planner = "Planner";
    public const string Chauffeur = "Chauffeur";
    public const string ITBeheerder = "ITBeheerder";
}

public sealed record Gebruiker(string Id, string Naam, IReadOnlySet<string> Rollen, string? Regio)
{
    public bool Is(string rol) => Rollen.Contains(rol);
}

public sealed record Rit
{
    public int Id { get; init; }
    public string Regio { get; init; } = "";
    public string ChauffeurId { get; init; } = "";
    public string Klant { get; init; } = "";
    public string Adres { get; init; } = "";
    public DateTimeOffset Vertrek { get; init; }
    public DateTimeOffset? Bevestigd { get; init; }
    public string? DocumentUrl { get; init; }
    public DateTimeOffset Aangemaakt { get; init; }
}

public sealed record AuditEntry(DateTimeOffset Tijd, string Gebruiker, string Actie, int? RitId);

public enum Uitkomst { Ok, NietGevonden, Verboden }

public interface INotifier
{
    Task SendAsync(string tekst, CancellationToken ct = default);
}

public interface IDocumentStore
{
    /// <summary>Slaat een vrachtdocument op en geeft de (web)locatie terug.</summary>
    Task<string> SaveAsync(int ritId, string regio, DateTimeOffset datum, string bestandsnaam, Stream inhoud, CancellationToken ct = default);
}
