#!/usr/bin/env python3
"""Audit a merge resolution for silent overrides of a main decision.

Extracted from the inline check in
docs/plans/2026-09-23-admin-console-main-merge-implementation-plan.md
(Task 13, Step 5), which verified the admin-console-landing-page merge into
main (fork-base 9eb99f76, main pinned at 65cdf63a). That check is reusable
for any "-X theirs"-style resolution of a long-lived branch against main:
it flags every place the resolution touches a line main itself decided,
after main and the branch forked.

A main decision is a line main changed after the fork, so the check is
line-level: blame each line still present at --main-pin and ask whether its
last commit predates --fork-base. A file-level diff would flag branch edits
to pre-fork lines, which merge cleanly and override nothing.

Three finding classes, all meaning "a human should look at this line" --
none of them proves an override on their own, the same way the original
two-class version never claimed to:

  MAIN LINE CHANGED       An existing line the resolution touches was last
                           blamed to a commit after --fork-base at
                           --main-pin -- main changed it post-fork, and the
                           resolution changed it again.
  MAIN-DELETED LINE RE-ADDED
                           A line main explicitly deleted between
                           --fork-base and --main-pin reappears in the
                           resolution.
  NEW LINE IN MAIN-TOUCHED FILE
                           A line the resolution inserts with no
                           corresponding old line to blame (a pure
                           insertion), in a file main itself changed after
                           the fork. This is the gap the two-class version
                           had: its blame walk only visits hunks where
                           `newCount > 0`, i.e. hunks that keep or replace
                           at least one old line (`b == 0` skips a pure
                           insertion outright, since there is nothing at
                           --main-pin for `git blame` to attribute). A
                           brand-new line can override a main decision
                           without editing any of main's existing lines --
                           new code around them is enough -- and the
                           original check had no way to see that. This
                           class closes exactly that gap. It is still not a
                           semantic check: it cannot tell whether a given
                           insertion actually conflicts with what main
                           decided, only that it landed in a file main was
                           actively changing. Scoped to files in the
                           intersection already computed for the other two
                           classes, so it does not add noise from files
                           main never touched.

Usage:
    python3 scripts/verify-merge-sanctioned-sites.py \\
        --fork-base 9eb99f76 --main-pin 65cdf63a [--tip HEAD]

Prints one line per finding to stdout, one class label per line, exactly as
the original inline check did. Empty output is a clean audit. This script
does not interpret its own output -- cross-check every line it prints
against the merge's own sanctioned-sites list, same as the original.
"""

import argparse
import re
import subprocess
import sys


def sh(*args: str) -> str:
    return subprocess.run(args, capture_output=True, text=True).stdout


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--fork-base", required=True, help="commit where main and the branch diverged")
    parser.add_argument("--main-pin", required=True, help="main's tip the merge resolution was pinned against")
    parser.add_argument("--tip", default="HEAD", help="the resolved merge commit to audit (default HEAD)")
    args = parser.parse_args()

    fork_base, main_pin, tip = args.fork_base, args.main_pin, args.tip

    prefork_cache: dict[str, bool] = {}

    def prefork(commit: str) -> bool:
        if commit not in prefork_cache:
            prefork_cache[commit] = (
                subprocess.run(
                    ["git", "merge-base", "--is-ancestor", commit, fork_base]
                ).returncode
                == 0
            )
        return prefork_cache[commit]

    main_touched_files = set(sh("git", "diff", "--name-only", fork_base, main_pin).split())
    resolution_touched_files = set(sh("git", "diff", "--name-only", main_pin, tip).split())
    files = sorted(main_touched_files & resolution_touched_files)

    findings = 0
    for f in files:
        resolution_diff = sh("git", "diff", "-U0", main_pin, tip, "--", f)

        for hunk in re.finditer(r"^@@ -(\d+)(?:,(\d+))? \+\d+(?:,\d+)? @@", resolution_diff, re.M):
            old_start, old_count = int(hunk[1]), int(hunk[2] or 1)
            if old_count == 0:
                # Pure insertion: nothing existing to blame. This is the branch
                # the two-class version skipped outright -- see NEW LINE below.
                continue
            for line in sh(
                "git", "blame", "-l", "-s", "-L",
                f"{old_start},{old_start + old_count - 1}", main_pin, "--", f,
            ).splitlines():
                sha, rest = line.split(" ", 1)
                if not prefork(sha.lstrip("^")):
                    print("MAIN LINE CHANGED:", f, rest.strip()[:90])
                    findings += 1

        main_deleted = {
            line[1:].strip()
            for line in sh("git", "diff", "-U0", fork_base, main_pin, "--", f).splitlines()
            if line[:1] == "-" and line[:3] != "---"
        }
        for line in resolution_diff.splitlines():
            s = line[1:].strip()
            if line[:1] == "+" and line[:3] != "+++" and len(s) > 3 and s in main_deleted:
                print("MAIN-DELETED LINE RE-ADDED:", f, s[:90])
                findings += 1

        # NEW LINE IN MAIN-TOUCHED FILE: pure-insertion hunks (old_count == 0
        # above) in a file main itself changed since the fork -- the class the
        # original check had no way to see. `main_deleted` lines are excluded
        # since those are already reported above as re-additions, not new
        # content; a plain "already reported" line staying out of this class
        # keeps the two classes disjoint.
        for hunk in re.finditer(r"^@@ -(\d+)(?:,(\d+))? \+\d+(?:,\d+)? @@\n((?:\+.*\n?)*)", resolution_diff, re.M):
            old_count = int(hunk[2] or 1)
            if old_count != 0:
                continue
            for line in hunk[3].splitlines():
                if line[:1] != "+" or line[:3] == "+++":
                    continue
                s = line[1:].strip()
                if len(s) > 3 and s not in main_deleted:
                    print("NEW LINE IN MAIN-TOUCHED FILE:", f, s[:90])
                    findings += 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
