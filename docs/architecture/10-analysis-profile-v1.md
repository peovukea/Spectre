# Analysis Profile v1

## Purpose

This document freezes the current intended analytical behavior separately from
the target architecture. The greenfield implementation should reproduce this
profile before introducing algorithm changes. Tests and canonical result hashes,
not this prose alone, are the final executable specification.

## Default Parameters

| Parameter | Profile v1 default |
|---|---:|
| Window size | 10 minutes |
| Allowed lateness | 10 minutes |
| Evidence pointers per predicate interaction | 3 |
| Disparity alpha | 0.05 |
| Evidence pointers per consolidated edge | 3 |
| Family isolation | Enabled |
| Late-data policy | Drop and count |

Store all effective parameters with every run. Changing any result-affecting
parameter changes the analysis-profile hash even when the profile name remains
v1.

## Source and Identity Semantics

- A logical family contains a `.bin` base and contiguous `.bin.N` segments.
- Families and segments are processed in deterministic manifest order.
- A source location identifies the logical segment and the previous Avro sync
  block. It is not an exact per-record byte position.
- CDM UUID fixed bytes convert to a .NET GUID using big-endian UUID semantics.
- Profile v1 expects the CDM18 `TCCDMDatum` writer-schema full name.

Public evidence uses opaque source and segment IDs. An authorized operator can
resolve them to physical locations through the input catalog.

## Supported Normalized Datums

### Events

An event contributes:

- optional event ID;
- optional subject ID;
- zero, one, or two predicate-object IDs;
- event type string;
- Unix timestamp nanoseconds;
- source evidence.

An event with no subject is skipped. An event with neither predicate object is
skipped. Otherwise emit one directed edge for each present predicate object in
first-object then second-object order.

### Entities

Profile v1 supports:

- Subject;
- FileObject;
- NetFlowObject;
- UnnamedPipeObject;
- MemoryObject;
- RegistryKeyObject;
- PacketSocketObject;
- SrcSinkObject.

Each valid entity emits `HAS_CDM_TYPE`, `HAS_NODE_KIND`, then normalized scalar
attributes in ordinal predicate order. Subject kinds map to process, thread,
unit, or basic block. Unknown subject subtypes fall back to process, retain the
raw subtype attribute, and increment a diagnostic counter.

Other valid CDM record types are unsupported and counted. A supported record
missing a required UUID or failing required UUID conversion is malformed and
counted.

The target static/generated mapper must preserve profile v1 attribute names and
formatting. A cleaner attribute model is a future profile unless golden results
prove it equivalent.

## Metadata Resolution

Entity attributes update node metadata in observed input order. Metadata has no
event time in profile v1 and affects only subsequently processed edges.

Semantic extraction resolves canonical metadata names from these forms in
priority order:

1. direct canonical predicate;
2. `HAS_PROPERTY_` form;
3. `HAS_BASE_PROPERTY_` form.

The profile uses node kind, path/file path, remote port, and remote IP/address.
Missing node kind resolves to `UNKNOWN` and is counted.

## Term Extraction

For an edge from source to target with predicate `P`:

### Source document

- `OUT:P`;
- `OUT:P:TO_KIND:<target-kind>`;
- optional target path bucket;
- optional target remote-port bucket;
- optional target remote-IP-scope bucket.

### Target document

- `IN:P`;
- `IN:P:FROM_KIND:<source-kind>`;
- optional source path bucket;
- optional source remote-port bucket;
- optional source remote-IP-scope bucket.

A self-edge adds both outgoing and incoming terms to one document. Interaction
term counts contain both term sets.

Predicates and kinds are trimmed and uppercased.

### Path buckets

Normalize slashes and lowercase, then classify exact roots and descendants for:

- `/etc`, `/tmp`, `/home`, `/usr`, `/var`, `/bin`, `/sbin`, `/dev`, `/proc`;
- `OTHER` for another non-empty path;
- `UNKNOWN` for empty input.

### Port buckets

- preserve ports 22, 53, 80, and 443;
- privileged `0-1023`;
- registered `1024-49151`;
- dynamic `49152-65535`;
- unknown for invalid input.

### IP-scope buckets

- loopback;
- private IPv4 for RFC1918 ranges used by the current implementation;
- public for other valid addresses;
- unknown for invalid input.

IPv6 private/link-local classification is not distinguished in profile v1 and
should be considered for a later profile.

## Windows and Watermarks

Window start is the mathematical floor of the event timestamp divided by the
window duration, multiplied by the duration. This definition includes negative
timestamps. Window end is start plus duration with checked arithmetic.

For each family:

1. track the maximum timestamp observed;
2. watermark equals maximum timestamp minus allowed lateness;
3. close every open window whose end is at or before the watermark;
4. a fact targeting a window at or before the closed-through boundary is late;
5. late facts are dropped and counted with bounded lateness distribution;
6. family completion flushes remaining windows chronologically;
7. all watermark and corpus state resets before the next family.

The system does not synthesize empty semantic windows for time ranges with no
events. The disparity stage does emit an empty backbone slice when a semantic
slice contains no retained edges, preserving the timeline of emitted semantic
windows.

## Behavioral Documents

Each node with an interaction in a window has one behavioral document containing
its term counts and the node kind known when the document is first created.

Documents are finalized in canonical node-ID order. Jaccard values are computed
before current-window baselines are updated.

### Node-kind baseline

For each node kind, profile v1 maintains the union of all term sets from prior
emitted documents in the current family. The document compares its term set to
that accumulated union. The first document for a kind has no value.

### Previous-self baseline

For each node, retain the term set from its most recently emitted document in
the current family. A new document compares to that set. The first document for
the node has no value.

### Jaccard

Jaccard equals intersection size divided by union size. Two empty sets yield
one. Profile v1 compares term presence, not term frequency or TF-IDF weight.

## TF-IDF

For each emitted window, profile v1:

1. adds every finalized document to the family document count;
2. increments document frequency once for each term present in each document;
3. computes weights using the updated rolling corpus, including the current
   window.

Term frequency for positive count `c` is:

`TF(c) = 1 + ln(c)`

Inverse document frequency for rolling document count `N` and term document
frequency `df` is:

`IDF(term) = ln((N + 1) / (df + 1)) + 1`

Document weight equals `TF * IDF` for each document term.

A predicate-level interaction's semantic weight is the sum of `TF * IDF` over
its combined interaction term counts. If that sum is not positive, profile v1
falls back to the interaction event count.

Because the corpus is rolling and family-local, changing family boundaries or
window emission order changes results. Both are part of the profile.

## Predicate Interactions and Evidence

Within a semantic window, group by source ID, target ID, and predicate. Count
events, accumulate combined source/target term counts, and retain up to the
configured evidence limit in observed order.

Final interactions are ordered by source ID, target ID, then predicate.

## Disparity Filtering

First consolidate all predicate interactions for the same directed source and
target into one candidate:

- sum event counts;
- sum semantic weights;
- retain predicate counts and predicate semantic weights;
- sum term counts;
- union evidence before final deterministic truncation.

For every node, build:

- an outgoing population of candidates where it is source;
- an incoming population of candidates where it is target.

For candidate weight `w`, endpoint strength `s`, and degree `k`:

`normalized = clamp(w / s, 0, 1)`

For `k > 1`:

`significance = (1 - normalized) ^ (k - 1)`

The direction is significant when `significance < alpha`.

For `k = 1`, significance is absent and the direction is not automatically
significant. Retain the candidate if either source-outgoing or target-incoming
direction is significant.

Reject invalid non-positive counts/weights, missing endpoint documents,
non-finite sums, and arithmetic overflow rather than persisting invalid scores.

## Backbone Output

For every emitted semantic slice, output one backbone slice containing:

- the same family and window identity;
- documents for endpoints of retained interactions only;
- retained interactions in canonical source/target order;
- indexing metric snapshot;
- disparity metric snapshot;
- reduction counts and source/retained semantic weights.

Evidence is ordered by present timestamp first, timestamp, logical source
segment, source position, present event ID first, and event ID, then truncated to
the configured consolidated-edge limit.

## Determinism Requirements

- Manifest family and segment order is fixed.
- Input record order within a segment is preserved.
- Stage queues do not reorder facts within a family.
- Window emission is chronological.
- Maps and sets are canonicalized at output/hash boundaries.
- Numeric formulas and finite/overflow policy are versioned.
- Family and slice result hashes are stable for identical input/profile/build.

Run the same manifest at different permitted family concurrency settings and
require identical promoted hashes.

## Candidate Profile v2 Improvements

These changes are intentionally outside v1 and require new goldens:

- metadata prepass or bounded unresolved-edge enrichment;
- corrected private/link-local IPv6 buckets;
- configurable session/sliding windows;
- reopening or side-output for late facts;
- fixed reference corpus or global corpus TF-IDF;
- weighted-vector similarity beyond term-set Jaccard;
- alternative degree-one disparity policy;
- additional CDM record/entity support;
- normalized typed attributes rather than flattened strings.
