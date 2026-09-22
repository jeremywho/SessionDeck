"""Attach to a SessionDeck pty host from the command line and dump its scrollback.

    python tools/host-probe.py <hosts-dir>/<id>.json [--follow] [--send TEXT]

No dependencies beyond the standard library: it speaks just enough WebSocket to read frames.
"""
import base64
import io
import json
import os
import socket
import struct
import sys
import time


def handshake(port, token, after=0):
    s = socket.create_connection(("127.0.0.1", port), timeout=5)
    key = base64.b64encode(os.urandom(16)).decode()
    req = (f"GET /attach?token={token}&after={after} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n"
           f"Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n")
    s.sendall(req.encode())
    head = b""
    while b"\r\n\r\n" not in head:
        chunk = s.recv(4096)
        if not chunk:
            raise SystemExit("closed during handshake")
        head += chunk
    status = head.split(b"\r\n", 1)[0].decode()
    if "101" not in status:
        raise SystemExit(f"handshake failed: {status}")
    return s, head.split(b"\r\n\r\n", 1)[1]


def recv_exact(s, n, buf):
    while len(buf) < n:
        chunk = s.recv(65536)
        if not chunk:
            return None, buf
        buf += chunk
    return buf[:n], buf[n:]


def read_frame(s, buf):
    hdr, buf = recv_exact(s, 2, buf)
    if hdr is None:
        return None, None, buf
    fin_op, ln = hdr[0], hdr[1] & 0x7F
    op = fin_op & 0x0F
    if ln == 126:
        b, buf = recv_exact(s, 2, buf)
        ln = struct.unpack(">H", b)[0]
    elif ln == 127:
        b, buf = recv_exact(s, 8, buf)
        ln = struct.unpack(">Q", b)[0]
    payload, buf = recv_exact(s, ln, buf)
    return op, payload, buf


def send_text(s, text):
    data = text.encode()
    mask = os.urandom(4)
    hdr = bytes([0x81])
    if len(data) < 126:
        hdr += bytes([0x80 | len(data)])
    else:
        hdr += bytes([0x80 | 126]) + struct.pack(">H", len(data))
    masked = bytes(b ^ mask[i % 4] for i, b in enumerate(data))
    s.sendall(hdr + mask + masked)


def main():
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
    args = sys.argv[1:]
    if not args:
        raise SystemExit(__doc__)
    rec = json.load(open(args[0]))
    follow = "--follow" in args
    send = None
    if "--send" in args:
        send = args[args.index("--send") + 1]
    s, buf = handshake(rec["Port"], rec["Token"])
    if "--cols" in args and "--rows" in args:
        send_text(s, json.dumps({"type": "resize", "cols": int(args[args.index("--cols") + 1]), "rows": int(args[args.index("--rows") + 1])}))
    if send is not None:
        send_text(s, json.dumps({"type": "input", "data": send}))
    s.settimeout(2 if not follow else 60)
    deadline = time.time() + (1.5 if not follow else 1e9)
    while True:
        try:
            op, payload, buf = read_frame(s, buf)
        except socket.timeout:
            if time.time() > deadline:
                break
            continue
        if op is None:
            print("\n[socket closed]")
            break
        if op == 0x1:
            print(f"\n[text] {payload.decode(errors='replace')}")
        elif op == 0x2:
            sys.stdout.write(payload.decode("utf-8", errors="replace"))
            sys.stdout.flush()
        elif op == 0x8:
            print("\n[close]")
            break
        if not follow and time.time() > deadline:
            break


if __name__ == "__main__":
    main()
