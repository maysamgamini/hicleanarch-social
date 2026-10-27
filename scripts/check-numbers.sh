#!/usr/bin/env bash
# Every number in docs/numbers.md says what it is (s3-l16): each line that holds a number, outside
# headings and code blocks, names its kind: "local measurement", "design estimate" or "learner
# exercise". A number without one is an unlabelled claim, and the check fails on it.
#   scripts/check-numbers.sh [file, default docs/numbers.md]
set -euo pipefail
file=${1:-"$(cd "$(dirname "$0")/.." && pwd)/docs/numbers.md"}
python3 - "$file" <<'PY'
import re, sys
path = sys.argv[1]
labels = ("local measurement", "design estimate", "learner exercise")
bad, fenced = [], False
for n, line in enumerate(open(path, encoding="utf-8"), 1):
    text = line.strip()
    if text.startswith("```"):
        fenced = not fenced
        continue
    if fenced or text.startswith("#") or not re.search(r"\d", text):
        continue
    # A table's header separator and a row of labels only are not claims.
    if re.fullmatch(r"\|[\s:|-]+\|", text):
        continue
    if not any(label in text.lower() for label in labels):
        bad.append(f"{path}:{n}: {text}")
if bad:
    print("Numbers without a label (local measurement, design estimate or learner exercise):")
    print("\n".join(bad))
    sys.exit(1)
print(f"{path}: every number is labelled")
PY
