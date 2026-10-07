# Ritplanning – proof-of-concept (fictieve casus Draloop Logistiek B.V.)

Gecontaineriseerde ritplanningmodule met Entra ID-login, rollen (Planner / Chauffeur / IT-beheerder),
Teams-melding, SharePoint-opslag van vrachtdocumenten, CI/CD met kwetsbaarhedenscan en automatische rollback.

## Wat er al getest is en wat NIET

| Onderdeel | Status |
|---|---|
| Domeinlogica (RBAC, auditlog, bewaartermijn 24 mnd, ERP-CSV) | **Getest**: 13 unit tests slagen (`dotnet run --project tests/Ritplanning.Tests`) |
| API-autorisatie (401/403, CSRF-header, rolfiltering) | **Getest** lokaal: 16 smoke-tests slagen, met *nagebootste* Easy Auth-headers (`scripts/smoke-test.sh`) |
| Health endpoint + bewust kapotte uitrol (`BREAK_HEALTH=true` geeft 503) | **Getest** lokaal |
| Dockerfile, GitHub Actions-workflow, Trivy | **Niet uitgevoerd** (geen Docker-daemon/internet-toegang in mijn omgeving) |
| Azure-scripts (`infra/*.sh`), `deploy.sh` met rollback | **Niet uitgevoerd**: alleen syntax gecontroleerd. Verwacht een paar aanpassingen |
| Echte Entra ID-login, MFA, Conditional Access, Teams, SharePoint, belasting op Azure | **Niet getest**: dat moet jij uitvoeren; dit zijn jouw testresultaten voor het verslag |

Lokale tests bewijzen dat **de app** de rollen correct afdwingt. Dat Entra ID/Conditional Access de login afdwingt
bewijs je met screenshots uit jouw tenant (T1, T2).

## Architectuurkeuzes (let op: wijkt op enkele punten af van het conceptverslag)

* **Login**: ingebouwde authenticatie van Azure Container Apps (Easy Auth) met Entra ID. De app leest de rollen uit
  de header `X-MS-CLIENT-PRINCIPAL`. Geen NuGet-packages nodig.
* **Teams**: Graph biedt **geen** applicatie-machtiging om in een kanaal te posten (`ChannelMessage.Send` is alleen
  delegated). Daarom: Teams Workflows-webhook; de URL staat als secret in Key Vault.
* **SharePoint**: Graph met managed identity en `Sites.Selected` (alleen die ene site).
* **Rollback**: blue/green via revisies. De nieuwe revisie krijgt 0% verkeer; pas na een geslaagde health check
  schakelt het verkeer. Bij falen blijft de vorige revisie live (`scripts/deploy.sh`).
* **Data**: in-memory met voorbeeldritten (PoC). In het verslag staat SQL Server; vermeld dat de PoC dit vervangt
  door in-memory opslag.
* **Planner-regio**: via claim `regio` of de instelling `REGIO_MAP="planner@x.nl=Eindhoven;..."`.

## Stappenplan

1. **Tenant en Azure**: Microsoft 365 Developer-tenant (E5) + Azure-abonnement (studentenkrediet of gratis proefperiode).
   Maak testgebruikers: een planner, twee chauffeurs, een IT-beheerder. Maak Entra-groepen *Planners*, *Chauffeurs*, *IT-beheerders*.
2. **Teams-webhook**: in Teams, kanaal > Workflows > "Post to a channel when a webhook request is received". Kopieer de URL en
   `export TEAMS_WEBHOOK_URL='...'`.
3. **Azure inrichten**: `bash infra/setup-azure.sh` (Cloud Shell, vanuit de repo-map). Noteer de uitvoer.
4. **Rollen toewijzen**: Entra-portal > Enterprise applications > *Ritplanning (Draloop PoC)* > Users and groups:
   groep Planners -> rol Planner, Chauffeurs -> Chauffeur, IT-beheerders -> IT-beheerder.
   Zet `DEMO_CHAUFFEURS` en `REGIO_MAP` op de Container App naar je echte testgebruikers (UPN's).
5. **SharePoint**: maak een site (bijv. *Planning*) en run `SITE_HOSTPAD=<tenant>.sharepoint.com:/sites/Planning bash infra/grant-graph.sh`.
6. **Conditional Access**: Entra-portal > Protection > Conditional Access > nieuw beleid "Ritplanning - vereis MFA en compliant apparaat":
   gebruikers = de drie groepen, doelapp = *Ritplanning (Draloop PoC)*, toegang verlenen = MFA (+ "compliant apparaat" voor IT-beheerders).
   Gebruik eerst *Report-only*, daarna *On*. (Compliant apparaat vereist Intune; zonder Intune alleen MFA gebruiken en dat in het verslag zo vermelden.)
7. **GitHub**: push deze map naar een repo, run `GH_REPO=jij/repo ACR=<acr> bash infra/setup-github-oidc.sh`, zet de secrets/variables.
   Push naar `main` start de pipeline.

## Testplan met bewijs (koppeling aan tabel in het verslag)

| Test | Uitvoeren | Bewijs verzamelen |
|---|---|---|
| T1 Login met MFA | Open de app-URL in een privévenster als planner | Screenshot MFA-prompt + `index.html` met naam en rol; Entra *Sign-in logs* |
| T2 Geweigerd door Conditional Access | Login vanaf een niet-conform apparaat/voorwaarde die je blokkeert | Screenshot foutmelding + sign-in log (Conditional Access = Failure) |
| T3 RBAC | Log in als chauffeur, planner, IT-beheerder | Screenshot van elk overzicht; `curl` naar `/api/beheer/audit` als chauffeur = 403 |
| T4 Geautomatiseerde uitrol | Commit een kleine wijziging naar `main` | Screenshot groene GitHub Actions-run + nieuwe revisie in Azure |
| T5 Scan blokkeert | Verwijs tijdelijk naar een oude basis-image (bijv. `aspnet:6.0`) in de Dockerfile | Screenshot rode Trivy-stap; zet daarna terug |
| T6 Belasting | `python3 scripts/loadtest.py --url https://<app>/api/ritten --cookie "AppServiceAuthSession=..."` | De uitvoertabel + aantal replica's (Azure-portal, *Metrics > Replica Count*). Vermeld meetopzet |
| T7 Rollback | Zet env var `BREAK_HEALTH=true` in een commit (of via `az containerapp update --set-env-vars`) en push | Pipeline-log met tijdstempels: health check faalt, verkeer blijft op de vorige revisie |

Pas in het verslag **alle** getallen (T6, "binnen 2 minuten" bij T7) aan naar wat jij meet.

## Lokaal draaien

```bash
dotnet run --project tests/Ritplanning.Tests                      # unit tests
ASPNETCORE_ENVIRONMENT=Development Auth__DevHeader=true \
  dotnet run --project src/Ritplanning.Web --urls http://localhost:8080
curl -H 'X-Dev-User: planner@draloop.test|Planner|Planner|Eindhoven' http://localhost:8080/api/ritten

./scripts/smoke-test.sh http://localhost:8080                     # tegen een Production-start
docker build -t ritplanning . && docker run -p 8080:8080 ritplanning
```

## Bewust vereenvoudigd

* Opslag is in-memory; herstart wist wijzigingen. Echte koppeling met SQL en ERP zijn vervolgstappen.
* Locatiegegevens tijdens de rit worden in deze PoC niet verwerkt (alleen route/klant/vertrek).
* Geen Intune-integratie (zie scope in het verslag).
