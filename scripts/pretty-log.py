#!/usr/bin/env python3
"""Turns Flexarr's JSON log lines (stdin) into readable text: `make logs`, or `... | scripts/pretty-log.py Warning`."""
import json
import sys

LEVELS = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"]
SHORT = {"Verbose": "VRB", "Debug": "DBG", "Information": "INF", "Warning": "WRN", "Error": "ERR", "Fatal": "FTL"}
minimum = LEVELS.index(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1] in LEVELS else 0

for line in sys.stdin:
    try:
        entry = json.loads(line)
    except ValueError:
        print(line, end="")
        continue
    level = entry.get("@l", "Information")
    if LEVELS.index(level) < minimum:
        continue
    time = entry.get("@t", "")[11:23]
    print(f"{time} {SHORT.get(level, level)} [{entry.get('Area', '')}] {entry.get('@m', '')}", flush=True)
    if "@x" in entry:
        print("    " + entry["@x"].replace("\n", "\n    "), flush=True)
