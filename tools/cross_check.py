"""Crossing check over a tools/netdump.lsp dump.

Two traces crossing is only a defect when they sit on the SAME copper layer. Crossings between
different layers are what a multilayer board is for, and they outnumber defects by ~400:1, so the
two are counted separately.

    python tools/cross_check.py <netdump.txt>

GOTCHA that this script exists to encode: do NOT bucket by raw elevation. Demo.cs draws top copper
at `ZTop + (seed % 5) * 0.004` -- FIVE micro-planes 0.004 apart, purely to stop coincident traces
z-fighting in the shaded view. Bucketing by elevation splits the top layer five ways and silently
reports zero crossings that are really there. Bucket by ELECTRICAL layer, using the same zclass
route_audit.py uses: z > -0.4 TOP, z > -1.2 MID, else BOT.

Reading the output:
  * Same-layer crossings are defects.
  * Traces of one NET never cross each other -- a net is a tree, later pins route to the net's
    existing cells and terminate on a shared pad. Crossing is what unrelated nets do.
  * Net COLOUR is per symbol table (TableColor), not per net, so two traces sharing a colour are
    not necessarily related.
"""
import sys
from collections import defaultdict

EPS_SHARED_VERTEX = 1e-6


def load(path):
    polys = []
    for line in open(path):
        line = line.strip()
        if not line.startswith('P '):
            continue
        parts = line.split()
        elev, width, color = float(parts[1]), float(parts[2]), int(parts[3])
        pts = [tuple(float(v) for v in p.split(',')) for p in parts[4:]]
        if len(pts) >= 2:
            polys.append((elev, width, color, pts))
    return polys


def layer_of(elev):
    """Electrical copper layer, not drawn elevation. See the module docstring."""
    return 'TOP' if elev > -0.4 else ('MID' if elev > -1.2 else 'BOT')


def orient(a, b, c):
    v = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
    return 0 if abs(v) < 1e-9 else (1 if v > 0 else -1)


def proper_cross(p1, p2, p3, p4):
    """True only for a genuine X crossing: shared endpoints and collinear touching do not count,
    because that is how a net's branches legitimately join its trunk."""
    for a in (p1, p2):
        for b in (p3, p4):
            if abs(a[0] - b[0]) < EPS_SHARED_VERTEX and abs(a[1] - b[1]) < EPS_SHARED_VERTEX:
                return False
    d1, d2 = orient(p3, p4, p1), orient(p3, p4, p2)
    d3, d4 = orient(p1, p2, p3), orient(p1, p2, p4)
    return d1 * d2 < 0 and d3 * d4 < 0


def intersection(p1, p2, p3, p4):
    (x1, y1), (x2, y2), (x3, y3), (x4, y4) = p1, p2, p3, p4
    d = (x2 - x1) * (y4 - y3) - (y2 - y1) * (x4 - x3)
    if abs(d) < 1e-12:
        return None
    t = ((x3 - x1) * (y4 - y3) - (y3 - y1) * (x4 - x3)) / d
    return (x1 + t * (x2 - x1), y1 + t * (y2 - y1))


def main(path):
    polys = load(path)
    segs_by_layer = defaultdict(list)
    for pi, (elev, width, color, pts) in enumerate(polys):
        key = layer_of(elev)
        for si in range(len(pts) - 1):
            segs_by_layer[key].append((pi, pts[si], pts[si + 1]))

    print('traces=%d' % len(polys))
    for k in sorted(segs_by_layer):
        print('  layer %s  segments=%d' % (k, len(segs_by_layer[k])))
    print()

    total = 0
    for k in sorted(segs_by_layer):
        segs = segs_by_layer[k]
        hits = []
        for i in range(len(segs)):
            pi, a, b = segs[i]
            for j in range(i + 1, len(segs)):
                pj, c, d = segs[j]
                if pi == pj:
                    continue            # a trace may touch itself at a bend
                if proper_cross(a, b, c, d):
                    hits.append((pi, pj, intersection(a, b, c, d)))
        total += len(hits)
        print('layer %s : %d SAME-LAYER crossings' % (k, len(hits)))
        for pi, pj, at in hits[:20]:
            # distance to the nearest vertex of either trace, in board units. A crossing within
            # about one grid cell (cs = 0.6) of a vertex is inside the pad/via escape, where
            # SegClearG deliberately skips the endpoint cell and blocked() exempts own cells.
            da = min(((at[0] - v[0]) ** 2 + (at[1] - v[1]) ** 2) ** .5 for v in polys[pi][3])
            db = min(((at[0] - v[0]) ** 2 + (at[1] - v[1]) ** 2) ** .5 for v in polys[pj][3])
            print('   #%d (w=%.2f) x #%d (w=%.2f) at (%.3f, %.3f)   nearest vertex: %.2f / %.2f'
                  % (pi, polys[pi][1], pj, polys[pj][1], at[0], at[1], da, db))
    print('TOTAL SAME-LAYER CROSSINGS = %d   <-- this is the defect count' % total)
    print()

    # different-layer crossings: legitimate, counted for scale
    all_segs = []
    for pi, (elev, width, color, pts) in enumerate(polys):
        for si in range(len(pts) - 1):
            all_segs.append((pi, layer_of(elev), color, pts[si], pts[si + 1]))
    pairs = defaultdict(int)
    same_color = 0
    for i in range(len(all_segs)):
        pi, li, ci, a, b = all_segs[i]
        for j in range(i + 1, len(all_segs)):
            pj, lj, cj, c, d = all_segs[j]
            if pi == pj or li == lj:
                continue
            if proper_cross(a, b, c, d):
                pairs[tuple(sorted((li, lj)))] += 1
                if ci == cj:
                    same_color += 1
    print('DIFFERENT-layer crossings (legitimate multilayer routing):')
    for k in sorted(pairs):
        print('   %s over %s : %d' % (k[1], k[0], pairs[k]))
    print('   TOTAL = %d' % sum(pairs.values()))
    print('   of those, both traces the same colour = %d (colour is per symbol table, not per net)'
          % same_color)


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else 'netdump.txt')
