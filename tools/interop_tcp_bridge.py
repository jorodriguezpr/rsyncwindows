"""Interop-test support tool for RsyncWindows.Interop.Tests (Phase 4a).

Bridges a TCP listener to a subprocess's stdin/stdout, one process per accepted
connection -- a stand-in for `nc -e` (not available: WSL2 Ubuntu's netcat is the OpenBSD
variant) or socat (not installed), used to let a Windows-side TCP client (the C# test)
talk to the real rsync --server process running under WSL2 without going through
wsl.exe's own process-stdio relay -- that relay was found to CORRUPT binary data when
invoked as a child process from a Windows .NET process in this environment (confirmed:
writing a known 4-byte value and observing the real rsync binary receive something else
and reject it with "protocol version mismatch", while a byte-identical native-WSL probe
using the same argv worked instantly). A plain TCP socket has no such translation layer.

Usage (run inside WSL2, from the repo root mounted at /mnt/c/...):
    python3 tools/interop_tcp_bridge.py 17900 <path-to-built-rsync> --server -e32.0fxCIvu . <dest-dir>
Then point RsyncWindows.Interop.Tests' PushSingleFileToRealRsyncTests at 127.0.0.1:17900
(NOT "localhost" -- see BridgeReachable()'s doc comment: localhost resolves to ::1 first
on this box, and the bridge only binds IPv4).

IMPORTANT: reads off the child's stdout use os.read(fd, n), NOT the buffered
`file.read(n)` -- the latter blocks until n bytes accumulate (or EOF), which silently
stalled every early version of this bridge even though the child had already written and
flushed its response; os.read returns as soon as any data is available, matching how a
real pipe/socket is meant to be drained."""
import os, socket, subprocess, sys, threading

port = int(sys.argv[1])
cmd = sys.argv[2:]

srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind(('0.0.0.0', port))
srv.listen(5)
print(f"listening on {port}, will exec per-connection: {cmd}", file=sys.stderr, flush=True)


def pump(name, src_read, dst_write, flush=None):
    try:
        while True:
            chunk = src_read(65536)
            if not chunk:
                print(f"{name}: EOF", file=sys.stderr, flush=True)
                break
            print(f"{name}: {len(chunk)} bytes: {chunk.hex()}", file=sys.stderr, flush=True)
            dst_write(chunk)
            if flush:
                flush()
    except (OSError, ValueError) as e:
        print(f"{name}: {e}", file=sys.stderr, flush=True)


def handle(conn, addr):
    print(f"accepted from {addr}", file=sys.stderr, flush=True)
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr)
    stdout_fd = proc.stdout.fileno()
    t1 = threading.Thread(target=pump, args=("client->rsync", conn.recv, proc.stdin.write, proc.stdin.flush), daemon=True)
    t2 = threading.Thread(target=pump, args=("rsync->client", lambda n: os.read(stdout_fd, n), conn.sendall), daemon=True)
    t1.start()
    t2.start()
    proc.wait()
    t2.join(timeout=2)
    print(f"process for {addr} exited with {proc.returncode}", file=sys.stderr, flush=True)
    conn.close()


while True:
    c, a = srv.accept()
    threading.Thread(target=handle, args=(c, a), daemon=True).start()
