#!/usr/bin/env python3
"""Forward the active Linux MPRIS player to a Lyrictified Wine instance."""
import argparse
import json
import socket
import subprocess
import time


def player_state():
    fmt = '{{title}}\t{{artist}}\t{{album}}\t{{mpris:length}}\t{{position}}\t{{status}}'
    try:
        raw = subprocess.check_output(["playerctl", "metadata", "--format", fmt], text=True, stderr=subprocess.DEVNULL).strip()
        title, artist, album, duration, position, status = (raw.split("\t", 5) + [""] * 6)[:6]
        return {"title": title, "artist": artist, "album": album,
                "duration": float(duration or 0) / 1_000_000,
                "position": float(position or 0) / 1_000_000, "status": status}
    except (subprocess.CalledProcessError, ValueError):
        return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--interval", type=float, default=0.25)
    args = parser.parse_args()
    while True:
        try:
            with socket.create_connection(("127.0.0.1", args.port)) as connection:
                last = None
                while True:
                    state = player_state()
                    if state and state != last:
                        connection.sendall((json.dumps(state) + "\n").encode())
                        last = state
                    time.sleep(args.interval)
        except (ConnectionRefusedError, BrokenPipeError, OSError):
            time.sleep(1)


if __name__ == "__main__":
    main()
