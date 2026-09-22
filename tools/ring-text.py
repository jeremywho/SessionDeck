"""Dump a host's scrollback as plain text: python tools/ring-text.py <host-record.json> [--head N] [--tail N]"""
import importlib.util
import io
import json
import re
import sys

spec = importlib.util.spec_from_file_location("hp", __file__.replace("ring-text.py", "host-probe.py"))
hp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hp)


def main():
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
    rec = json.load(open(sys.argv[1], encoding="utf-8"))
    head = int(sys.argv[sys.argv.index("--head") + 1]) if "--head" in sys.argv else 0
    tail = int(sys.argv[sys.argv.index("--tail") + 1]) if "--tail" in sys.argv else 40
    s, buf = hp.handshake(rec["Port"], rec["Token"])
    s.settimeout(1.5)
    raw = bytearray()
    while True:
        try:
            op, payload, buf = hp.read_frame(s, buf)
        except OSError:
            break
        if op is None or op == 0x8:
            break
        if op == 0x2:
            raw += payload
    text = raw.decode("utf-8", "replace")
    text = re.sub(r"\x1b\[[0-9;?]*[A-Za-z]", "", text)
    text = re.sub(r"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)", "", text)
    text = re.sub(r"\x1b[()][A-Za-z0-9]", "", text)
    lines = [l.rstrip() for l in text.replace("\r", "").split("\n") if l.strip()]
    if head:
        print("\n".join(lines[:head]))
        print("...")
    print("\n".join(lines[-tail:]))


if __name__ == "__main__":
    main()
