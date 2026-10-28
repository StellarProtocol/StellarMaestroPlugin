"""Maestro-Link sync spike: write two click-track MIDIs (stdlib only, hand-built SMF bytes).

click_a.mid / click_b.mid — format-0 SMF, 120 BPM, 480 TPQN, 1 bar (4/4) of silence, then 32 quarter notes of 100 ms.
  a = C5 (72), b = G4 (67)  — distinct pitches so a listener's diag log can tell the two performers apart even by note.
Note k (0-based) therefore starts at song time 2000 + 500*k ms (analyze.py assumes exactly this).

Usage:  python make_clicks.py [out_dir]     (default: this script's folder)
"""
import os
import struct
import sys

TPQN = 480
TEMPO_US = 500_000            # 120 BPM
LEAD_TICKS = 4 * TPQN         # one 4/4 bar of silence = 2000 ms
NOTE_TICKS = 96               # 100 ms at 120 BPM (480 ticks = 500 ms)
COUNT = 32
VELOCITY = 100


def vlq(n: int) -> bytes:
    """MIDI variable-length quantity."""
    out = [n & 0x7F]
    n >>= 7
    while n:
        out.append((n & 0x7F) | 0x80)
        n >>= 7
    return bytes(reversed(out))


def build(note: int) -> bytes:
    ev = bytearray()
    ev += vlq(0) + b"\xFF\x51\x03" + TEMPO_US.to_bytes(3, "big")        # tempo
    ev += vlq(0) + b"\xFF\x58\x04" + bytes([4, 2, 24, 8])               # 4/4
    delta = LEAD_TICKS
    for _ in range(COUNT):
        ev += vlq(delta) + bytes([0x90, note, VELOCITY])                 # note on, ch 0
        ev += vlq(NOTE_TICKS) + bytes([0x80, note, 0])                   # note off
        delta = TPQN - NOTE_TICKS
    ev += vlq(0) + b"\xFF\x2F\x00"                                       # end of track
    header = b"MThd" + struct.pack(">IHHH", 6, 0, 1, TPQN)
    track = b"MTrk" + struct.pack(">I", len(ev)) + bytes(ev)
    return header + track


def main() -> None:
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    os.makedirs(out_dir, exist_ok=True)
    for name, note in (("click_a.mid", 72), ("click_b.mid", 67)):
        path = os.path.join(out_dir, name)
        with open(path, "wb") as f:
            f.write(build(note))
        print(f"wrote {path}  (note {note}, {COUNT} clicks, first at 2000 ms, every 500 ms)")


if __name__ == "__main__":
    main()
