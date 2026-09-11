"""One-off diagnostic TCP proxy: listens locally, forwards to a real daemon, and logs every
byte in both directions (with a timestamp-ordered interleaved hex dump) to a file -- used to
get ground-truth wire bytes for comparing against what RsyncWindows.Core.FileList.FileListDecoder
actually consumes, when a real rsync client's push desyncs and pure source-reading isn't
pinning down the root cause.

Usage: python3 tools/hexdump_proxy.py <listen_port> <target_host> <target_port> <logfile>
"""
import socket, sys, threading, time

listen_port = int(sys.argv[1])
target_host = sys.argv[2]
target_port = int(sys.argv[3])
logfile = sys.argv[4]

log_lock = threading.Lock()

def log(direction, data):
    with log_lock:
        with open(logfile, "a") as f:
            f.write(f"\n=== {direction} {len(data)} bytes @ {time.time():.6f} ===\n")
            for i in range(0, len(data), 16):
                chunk = data[i:i+16]
                hexpart = " ".join(f"{b:02x}" for b in chunk)
                asciipart = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
                f.write(f"{i:04x}  {hexpart:<48}  {asciipart}\n")

def pump(src, dst, direction):
    try:
        while True:
            data = src.recv(65536)
            if not data:
                break
            log(direction, data)
            dst.sendall(data)
    except Exception as e:
        log(direction, f"EXCEPTION: {e}".encode())
    finally:
        try:
            dst.shutdown(socket.SHUT_WR)
        except Exception:
            pass

srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind(("0.0.0.0", listen_port))
srv.listen(1)
print(f"listening on {listen_port}, forwarding to {target_host}:{target_port}, logging to {logfile}")

client, addr = srv.accept()
upstream = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
upstream.connect((target_host, target_port))

t1 = threading.Thread(target=pump, args=(client, upstream, "CLIENT->SERVER"))
t2 = threading.Thread(target=pump, args=(upstream, client, "SERVER->CLIENT"))
t1.start()
t2.start()
t1.join()
t2.join()
print("done")
