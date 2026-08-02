#!/usr/bin/env python3
"""Forward the active Linux MPRIS player to a Lyrictified Wine instance."""
import argparse
import json
import socket
import subprocess
import sys
import time
from pathlib import Path


def player_state():
    fmt = '{{title}}\t{{artist}}\t{{album}}\t{{mpris:length}}\t{{position}}\t{{status}}'
    try:
        raw = subprocess.check_output(["playerctl", "metadata", "--format", fmt], text=True, stderr=subprocess.PIPE).strip()
        if not raw:
            print("[bridge] playerctl returned no active player metadata", file=sys.stderr)
            return None
        title, artist, album, duration, position, status = (raw.split("\t", 5) + [""] * 6)[:6]
        return {"title": title, "artist": artist, "album": album,
                "duration": float(duration or 0) / 1_000_000,
                "position": float(position or 0) / 1_000_000, "status": status}
    except FileNotFoundError:
        print("[bridge] playerctl was not found; install it with: sudo apt install playerctl", file=sys.stderr)
        raise SystemExit(1)
    except subprocess.CalledProcessError as error:
        print(f"[bridge] playerctl failed: {error.stderr.strip() or error}", file=sys.stderr)
        return None
    except ValueError as error:
        print(f"[bridge] could not parse playerctl output: {error}", file=sys.stderr)
        return None


def find_executable():
    candidates = [Path.cwd(), Path(__file__).resolve().parent]
    downloads = Path.home() / "Downloads"
    if downloads.exists():
        candidates.append(downloads)
    seen = set()
    for directory in candidates:
        try:
            directory = directory.resolve()
        except OSError:
            continue
        if directory in seen or not directory.exists():
            continue
        seen.add(directory)
        direct = directory / "Lyrictified.exe"
        if direct.is_file():
            return direct
        try:
            for path in directory.rglob("Lyrictified.exe"):
                if path.is_file():
                    return path
        except OSError:
            pass
    return None


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def choose_port():
    executable = find_executable()
    if executable:
        print(f"[bridge] found Wine executable: {executable}")
        answer = input("[bridge] Launch it automatically with Wine? [Y/n] ").strip().lower()
        if answer in ("", "y", "yes"):
            port = free_port()
            print(f"[bridge] launching on port {port}")
            try:
                subprocess.Popen(["wine", str(executable), "wine=1", f"port={port}"])
                return port
            except FileNotFoundError:
                print("[bridge] wine was not found; please install Wine or provide a port.")
    else:
        print("[bridge] could not find Lyrictified.exe automatically.")

    while True:
        value = input("[bridge] Enter the bridge port to connect to: ").strip()
        try:
            port = int(value)
            if 1 <= port <= 65535:
                return port
        except ValueError:
            pass
        print("[bridge] Please enter a TCP port between 1 and 65535.")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, help="Existing bridge port; omit to use interactive auto-launch")
    parser.add_argument("--interval", type=float, default=0.25)
    args = parser.parse_args()
    port = args.port or choose_port()
    print(f"[bridge] connecting to 127.0.0.1:{port}")
    while True:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=5) as connection:
                connection.settimeout(None)
                print("[bridge] connected; forwarding player changes")
                last = None
                while True:
                    state = player_state()
                    if state and state != last:
                        connection.sendall((json.dumps(state) + "\n").encode())
                        last = state
                    time.sleep(args.interval)
        except (ConnectionRefusedError, BrokenPipeError, OSError):
            print("[bridge] app is not accepting connections yet; retrying...", file=sys.stderr)
            time.sleep(1)


if __name__ == "__main__":
    main()
