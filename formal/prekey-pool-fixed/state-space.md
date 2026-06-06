# Pre-Key Pool Fixed — State Space

With inhibitor-arc gating, reachable markings:
- Pool=4 (initial), 3, 2, 5, 6 (cycling between low-pool refills and safe consumes).

`AG (P_Pool >= 1)` holds in every reachable marking.
Note: production verify.py needs inhibitor arc support added (open task).
TAPAAL handles inhibitor arcs natively.
