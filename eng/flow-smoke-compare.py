#!/usr/bin/env python3
"""Compares two eng/flow-smoke.sh outputs: every line must match once tick numbers are set aside, and
each tick may move by at most --slack (the loop drops a tick or two while loading).

usage: eng/flow-smoke-compare.py <before>/transitions.txt <after>/transitions.txt [--slack N]
"""
import re
import sys

TICK = re.compile(r"\d+")


def runs(path):
    """{run name: [lines]} from a transitions.txt."""
    result, name = {}, None
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        if line.startswith("== "):
            name = line[3:]
            result[name] = []
        elif name is not None and line and "Audio underrun" not in line:  # timing noise
            result[name].append(line)
    return result


def compare(before, after, slack):
    """The differences, one line each (empty: the same)."""
    problems = []
    for name in sorted(set(before) | set(after)):
        old, new = before.get(name), after.get(name)
        if old is None or new is None:
            problems.append(f"{name}: only in {'after' if old is None else 'before'}")
            continue
        if [TICK.sub("#", line) for line in old] != [TICK.sub("#", line) for line in new]:
            problems.append(f"{name}: lines differ")
            for line in old:
                if line not in new:
                    problems.append(f"  - {line}")
            for line in new:
                if line not in old:
                    problems.append(f"  + {line}")
            continue
        for a, b in zip(old, new):
            moved = [abs(int(x) - int(y)) for x, y in zip(TICK.findall(a), TICK.findall(b))]
            if any(delta > slack for delta in moved):
                problems.append(f"{name}: moved {max(moved)} ticks: {a!r} -> {b!r}")
    return problems


if __name__ == "__main__":
    if sys.argv[1:2] == ["--self-test"]:
        a = {"x": ["Showing gameplay at tick 100.", "Play results: Oni 1000000 1/0/0"]}
        assert compare(a, {"x": ["Showing gameplay at tick 101.", "Play results: Oni 1000000 1/0/0"]}, 3) == []
        assert compare(a, {"x": ["Showing gameplay at tick 110.", "Play results: Oni 1000000 1/0/0"]}, 3)
        assert compare(a, {"x": ["Showing result at tick 100.", "Play results: Oni 1000000 1/0/0"]}, 3)
        # A score is a number too: any change beyond the slack shows.
        assert compare(a, {"x": ["Showing gameplay at tick 100.", "Play results: Oni 990000 1/0/0"]}, 3)
        print("ok")
        sys.exit(0)
    slack = int(sys.argv[sys.argv.index("--slack") + 1]) if "--slack" in sys.argv else 3
    problems = compare(runs(sys.argv[1]), runs(sys.argv[2]), slack)
    print("\n".join(problems) if problems else f"same (ticks within {slack})")
    sys.exit(1 if problems else 0)
