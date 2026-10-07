#!/usr/bin/env python3
"""Eenvoudige belastingtest (alleen standaardbibliotheek).

Gebruik:
  python3 scripts/loadtest.py --url https://<app>.azurecontainerapps.io/api/ritten \
      --cookie "AppServiceAuthSession=<waarde uit je browser na inloggen>" \
      --users 10 50 100 150 --duur 30

Elke 'gebruiker' is een thread die tijdens --duur seconden achter elkaar verzoeken doet.
Uitvoer: gemiddelde en 95e percentiel responstijd (ms), aantal verzoeken en foutpercentage per stap.
Noteer ook in Azure (Container App > Revisions/replicas) hoeveel replica's er zijn gestart.
Meet NIET vanaf een langzaam netwerk of wifi en noem de meetopzet in je verslag.
"""
import argparse, statistics, threading, time, urllib.request, urllib.error

def worker(url, headers, einde, tijden, fouten, lock):
    while time.time() < einde:
        t0 = time.perf_counter()
        try:
            req = urllib.request.Request(url, headers=headers)
            with urllib.request.urlopen(req, timeout=30) as r:
                r.read()
                ok = r.status == 200
        except Exception:
            ok = False
        dt = (time.perf_counter() - t0) * 1000
        with lock:
            tijden.append(dt)
            if not ok:
                fouten.append(1)

def stap(url, headers, users, duur):
    tijden, fouten, lock = [], [], threading.Lock()
    einde = time.time() + duur
    threads = [threading.Thread(target=worker, args=(url, headers, einde, tijden, fouten, lock)) for _ in range(users)]
    [t.start() for t in threads]; [t.join() for t in threads]
    tijden.sort()
    p95 = tijden[int(len(tijden) * 0.95) - 1] if tijden else float("nan")
    return len(tijden), statistics.mean(tijden) if tijden else float("nan"), p95, 100 * len(fouten) / max(1, len(tijden))

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", required=True)
    ap.add_argument("--cookie", default="")
    ap.add_argument("--header", action="append", default=[], help="extra header, bijv. 'X-MS-CLIENT-PRINCIPAL: ...'")
    ap.add_argument("--users", type=int, nargs="+", default=[10, 50, 100, 150])
    ap.add_argument("--duur", type=int, default=30)
    a = ap.parse_args()
    headers = {"Cookie": a.cookie} if a.cookie else {}
    for h in a.header:
        k, v = h.split(":", 1); headers[k.strip()] = v.strip()
    print(f"{'gebruikers':>10} {'verzoeken':>10} {'gem. ms':>9} {'p95 ms':>9} {'fouten %':>9}")
    for u in a.users:
        n, gem, p95, f = stap(a.url, headers, u, a.duur)
        print(f"{u:>10} {n:>10} {gem:>9.0f} {p95:>9.0f} {f:>9.1f}")
