"""Maestro-Link sync spike analyzer (stdlib only).

Usage:  python analyze.py diag-*.tsv [--id N] [--lead 2000] [--step 500]

Reads each client's diag-<name>.tsv (written by LinkSpikeDiagPatch) and, for the chosen play command (default: the
LAST "play" CMD in each file), reports:
  * PLAY  — per entity, the k-th note heard; per-k delta between every pair of entities (mean/median/max |d|, ms) and each
            entity's effective delay = playedAt - (T + lead + k*step)   (click tracks: lead 2000, step 500).
  * RECV  — per entity, the k-th Note/Press record's stamped playTime; per-k playTime delta between entities (should be ~0
            if both performers honoured T), how early records arrived (playTime - recvAt), and per-k schedule delay
            (playedAt - playTime) = the game's band delay as actually applied.

Row formats (tab-separated):
  CMD   serverNow  id  op  T  name
  PLAY  serverNow  uuid  isSelf  note
  RECV  serverNow  uuid  isSelf  syncType  playType  playParam  playTime
EInstrumentSyncType: Note=0 Technique=1 Tone=2 SustainPedal=3.  EInstrumentPlayType: None=0 Press=1 Release=2.
"""
import glob
import statistics
import sys
from itertools import combinations

SYNC_NOTE, PLAY_PRESS = 0, 1


def stats(xs):
    if not xs:
        return "n=0"
    a = [abs(x) for x in xs]
    return (f"n={len(xs)} mean={statistics.mean(xs):+.1f} median={statistics.median(xs):+.1f} "
            f"min={min(xs):+d} max={max(xs):+d} max|d|={max(a)} "
            f"sd={statistics.pstdev(xs):.1f}")


def parse_args(argv):
    files, opts = [], {"id": None, "lead": 2000, "step": 500}
    it = iter(argv)
    for a in it:
        if a in ("--id", "--lead", "--step"):
            opts[a[2:]] = int(next(it))
        else:
            files.extend(sorted(glob.glob(a)) or [a])
    return files, opts


def load(path):
    rows = []
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            p = line.rstrip("\r\n").split("\t")
            if p and p[0] in ("CMD", "PLAY", "RECV"):
                rows.append(p)
    return rows


def segment(rows, want_id):
    """Rows after the chosen play CMD, up to the next CMD. Returns (T, cmd_id, rows)."""
    starts = [i for i, r in enumerate(rows) if r[0] == "CMD" and len(r) >= 5 and r[3] == "play"
              and (want_id is None or int(r[2]) == want_id)]
    if not starts:
        return None, None, []
    i = starts[-1]
    T, cid = int(rows[i][4]), int(rows[i][2])
    seg = []
    for r in rows[i + 1:]:
        if r[0] == "CMD":
            break
        seg.append(r)
    return T, cid, seg


def by_entity(seq):
    d = {}
    for uuid, t, extra in seq:
        d.setdefault(uuid, []).append((t, extra))
    for v in d.values():
        v.sort()
    return d


def analyze(path, opts):
    rows = load(path)
    T, cid, seg = segment(rows, opts["id"])
    print(f"\n=== {path}")
    if T is None:
        print("  no matching play CMD row")
        return
    print(f"  cmd id={cid}  T={T}  rows={len(seg)}")
    selfs = {}

    plays = []
    for r in seg:
        if r[0] == "PLAY" and len(r) >= 5:
            plays.append((r[2], int(r[1]), int(r[4])))
            selfs[r[2]] = r[3] == "1"
    P = by_entity(plays)

    recvs, seen = [], set()
    for r in seg:
        if r[0] == "RECV" and len(r) >= 8 and int(r[4]) == SYNC_NOTE and int(r[5]) == PLAY_PRESS:
            key = (r[2], r[6], r[7])
            if key in seen:      # same record twice (shouldn't happen) → count once
                continue
            seen.add(key)
            recvs.append((r[2], int(r[7]), int(r[1])))   # (uuid, playTime, recvAt)
            selfs[r[2]] = r[3] == "1"
    R = by_entity(recvs)

    def label(u):
        return f"{u}{' (self/echo)' if selfs.get(u) else ''}"

    print("  -- PLAY (heard) --")
    for u, v in P.items():
        notes = sorted({n for _, n in v})
        eff = [t - (T + opts["lead"] + k * opts["step"]) for k, (t, _) in enumerate(v)]
        print(f"  entity {label(u)}: {len(v)} notes, pitches={notes}")
        print(f"    effective delay (playedAt - (T+lead+k*step)): {stats(eff)}")
    for a, b in combinations(P.keys(), 2):
        n = min(len(P[a]), len(P[b]))
        d = [P[a][k][0] - P[b][k][0] for k in range(n)]
        print(f"  delta {a} - {b} per k: {stats(d)}")

    if R:
        print("  -- RECV (arrived) --")
        for u, v in R.items():
            early = [pt - at for pt, at in v]
            print(f"  entity {label(u)}: {len(v)} note presses; arrived early by (playTime-recvAt): {stats(early)}")
            print(f"    stamped offset (playTime - (T+lead+k*step)): "
                  f"{stats([pt - (T + opts['lead'] + k * opts['step']) for k, (pt, _) in enumerate(v)])}")
            if u in P:
                n = min(len(v), len(P[u]))
                print(f"    schedule delay (playedAt - playTime): {stats([P[u][k][0] - v[k][0] for k in range(n)])}")
        for a, b in combinations(R.keys(), 2):
            n = min(len(R[a]), len(R[b]))
            d = [R[a][k][0] - R[b][k][0] for k in range(n)]
            print(f"  playTime delta {a} - {b} per k: {stats(d)}")


def main():
    files, opts = parse_args(sys.argv[1:])
    if not files:
        print(__doc__)
        sys.exit(1)
    for f in files:
        analyze(f, opts)


if __name__ == "__main__":
    main()
