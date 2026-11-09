---
name: four-digit-patch-versions
description: from 0.14.3100 on, releases keep a four-digit patch number; it is deliberate, not a typo, and still x.y.z
type: project
updated: 2026-11-09
source: session
evidence: user decision 2026-11-09, on the 0.14.3100 release after 0.14.2; scripts/release-channel.mjs:RELEASE_VERSION
---

Releases use a four-digit patch number starting with 0.14.3100, and later releases continue that way. The jump from 0.14.2
is intended. The version is still three segments, so the x.y.z rule is unchanged: `channelOf('0.14.3100')` returns `stable`,
and a four-segment version such as `0.14.3100.1` is refused.

**Why:** the user decided this on 2026-11-09 and did not give a reason.

**How to apply:** do not "correct" such a version or flag it as a typo. When bumping, keep the patch four digits. How the
number is chosen from one release to the next was not stated, so ask rather than assume.
