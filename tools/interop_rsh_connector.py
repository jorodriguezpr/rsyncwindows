"""Interop-test support tool for RsyncWindows.Interop.Tests (Phase 4b).

Stands in for `ssh` via rsync's `--rsh` option, letting a REAL rsync client (running under
WSL2) connect to OUR C# server code (listening on a plain TCP port on the Windows host)
without needing an actual SSH server anywhere. Real rsync's do_cmd() (main.c:517) execs:

    <rsh-value-tokens> <machine> <rsync-path> --server [--sender] <bundled-flags> . <path>

So if invoked as `rsync ... --rsh="python3 tools/interop_rsh_connector.py <winIP> <port>" ...`,
this script's sys.argv is [self, winIP, port, machine, rsync-path, --server, ...] -- argv[1:2]
are the connection target we actually use; argv[3:] (the "remote command" a real ssh would
exec on the far end) is deliberately ignored, since the RECEIVING side here is our C# server
code, already listening and already configured for the test, not a process this script spawns.
This script's only job is to become a transparent byte pipe between rsync's stdin/stdout and
that TCP connection -- exactly what ssh itself would be doing if one were available.

Usage (from WSL2, with our C# test listening on the Windows host):
    rsync -a <file> --rsh="python3 tools/interop_rsh_connector.py <windows-host-ip> <port>" \\
        fakehost:<dest-path>

Windows host IP from WSL2: `ip route show default | awk '{print $3}'`.

Uses os.read()/plain socket recv(), not buffered file objects -- see
tools/interop_tcp_bridge.py's doc comment for why that distinction mattered here before."""
import os
import socket
import sys
import threading

host = sys.argv[1]
port = int(sys.argv[2])
# sys.argv[3:] (machine, rsync-path, --server, ...) intentionally unused -- see module doc.

sock = socket.create_connection((host, port))
stdin_fd = sys.stdin.fileno()
stdout_fd = sys.stdout.fileno()


def pump(read_fn, write_fn):
    while True:
        chunk = read_fn(65536)
        if not chunk:
            break
        write_fn(chunk)


t = threading.Thread(target=pump, args=(lambda n: os.read(stdin_fd, n), sock.sendall), daemon=True)
t.start()
pump(sock.recv, lambda b: os.write(stdout_fd, b))
sock.close()
