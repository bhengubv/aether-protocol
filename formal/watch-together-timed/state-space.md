# Watch-Together Timed — State Space

The timed reachability graph is a **zone graph** (TAPAAL semantics).
Zones encode possible time-valuations rather than single state-times.

For this 8-place, 3-transition model:
- ~5 distinct zone nodes
- Each query verified in <100ms by TAPAAL on Mac (M1)
- All 3 TCTL queries: SATISFIED

The ±100ms property is a structural consequence of the firing intervals
chosen. Tightening the intervals (e.g. 5-30ms) yields ±60ms. The model
is a **design tool**: change firing intervals to model real-world
network conditions, re-verify the SLA.
