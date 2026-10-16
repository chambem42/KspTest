"""A/B analysis against the run-to-run noise floor.

    python ab-noise-analysis.py <A1 dir> <B dir> <A2 dir>

A1 and A2 are two runs of the baseline build, B the candidate. For every deterministic recorded value the
difference B-A1 is set against the noise A2-A1. Each key is scaled so that near-zero quantities (a symmetric
plane's yaw moment at zero sideslip, a 1e-5 Cy) do not dominate: coefficients by 1e-3 minimum, forces and
moments by 1 kN / kN m minimum. Groups are reported with the max scaled difference for candidate and noise,
and the keys where the candidate moves more than 3x the noise and more than the group's tolerance.
"""
import json, sys, re, math
from collections import defaultdict

def load(d):
    j = json.load(open(d + '/result.json', encoding='utf-8'))
    vals = {}
    for t in j['tests']:
        for k, v in (t.get('values') or {}).items():
            try:
                vals[t['test'] + '.' + k] = float(v)
            except (TypeError, ValueError):
                pass
    return vals

a1, b, a2 = (load(d) for d in sys.argv[1:4])
keys = sorted(set(a1) & set(b) & set(a2))

def group_of(k):
    if '.hold' in k: return 'flown'
    test, craft, rest = k.split('.', 2)
    kind = rest.split('.')[0]
    if kind == 'visc': kind = 'visc.' + rest.split('.')[1]
    return f"{'editor' if 'Editor' in test else 'flight'}.{kind}"

def floor_of(k):
    if '.pred.' in k: return 1.0          # kN, kN m
    if '.voxel.' in k or '.sim.' in k or 'refArea' in k or 'massT' in k: return 1e-2
    if '.stab.' in k and re.search(r'\.d0[0-2]$|\.d2[4-6]$', k): return 1.0   # inertia tensor, t m2
    if '.stab.' in k and re.search(r'\.(area|MAC|b|nominalVelocity)$', k): return 1e-2
    return 1e-3                            # coefficients

groups = defaultdict(list)
for k in keys:
    if k.endswith('.samples') or k.endswith('.parts') or k.endswith('.wings'): continue
    if any(math.isnan(x) for x in (a1[k], b[k], a2[k])): continue
    scale = max(abs(a1[k]), abs(b[k]), abs(a2[k]), floor_of(k))
    groups[group_of(k)].append((k, (b[k] - a1[k]) / scale, (a2[k] - a1[k]) / scale, a1[k], b[k], a2[k]))

print(f"{'group':34} {'keys':>5} {'max|B-A1|':>10} {'max|A2-A1|':>11} {'rms|B-A1|':>10} {'rms|A2-A1|':>11}")
flagged = []
for g in sorted(groups):
    rows = groups[g]
    mb = max(abs(r[1]) for r in rows); ma = max(abs(r[2]) for r in rows)
    rb = math.sqrt(sum(r[1] ** 2 for r in rows) / len(rows)); ra = math.sqrt(sum(r[2] ** 2 for r in rows) / len(rows))
    print(f"{g:34} {len(rows):5d} {mb:10.2e} {ma:11.2e} {rb:10.2e} {ra:11.2e}")
    if g == 'flown': continue
    tol = 5e-3 if g.startswith('editor') else 1e-2
    for r in rows:
        if abs(r[1]) > tol and abs(r[1]) > 3 * max(abs(r[2]), ma * 0.5):
            flagged.append((g,) + r)

print()
if flagged:
    print(f"keys where the candidate moves beyond the noise ({len(flagged)}):")
    for g, k, db, da, va1, vb, va2 in sorted(flagged, key=lambda r: -abs(r[2]))[:60]:
        print(f"  {k:70} A1={va1:<12.6g} B={vb:<12.6g} A2={va2:<12.6g} B-A1={db:+.2e} A2-A1={da:+.2e}")
else:
    print("no deterministic key moves beyond the run-to-run noise")

print()
print("flown steady state (mean over the sampling window; scatter between the two baseline runs shown alongside):")
for k in keys:
    if '.hold' in k and re.search(r'\.(mach|aoa|Cl|Cd|dragRatioOfMeans|liftRatioOfMeans|deflectDeg)$', k):
        print(f"  {k:52} A1={a1[k]:<10.5g} B={b[k]:<10.5g} A2={a2[k]:<10.5g}")
