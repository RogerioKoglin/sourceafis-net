# Experimental development methodology

This clone is used to study SourceAFIS and to develop experimental matcher and
template-fusion features without silently changing the behavior already used by
DriverControlID.

## Test layers

1. **Original unit tests** protect low-level primitives and broad matching behavior.
2. **Golden characterization tests** freeze extraction, template topology,
   serialization, matching, score components, and simple 1:N ranking.
3. **DriverControlID integration build** proves that the application consumes this
   clone through `UseLocalSourceAfis=true`.
4. **Biometric laboratory evaluation** measures genuine and impostor distributions,
   identification rank, false negatives, false positives, and 1:N performance on a
   separate anonymized dataset.

Run the first three layers with:

```powershell
.\run-experimental-tests.ps1
```

Run only the SourceAFIS test suite with:

```powershell
dotnet test .\SourceAFIS.sln --configuration Release
```

Run only the golden characterization tests and print their diagnostic snapshot:

```powershell
dotnet test .\SourceAFIS.Tests\SourceAFIS.Tests.csproj `
  --configuration Release `
  --filter FullyQualifiedName~FingerprintAlgorithmCharacterizationTest `
  --logger "console;verbosity=normal"
```

## Metrics frozen by the golden tests

Extraction metrics:

- minutiae after skeleton collection;
- minutiae after inner-mask filtering;
- minutiae after cloud filtering;
- minutiae after the top-minutiae limit;
- final minutiae count;
- edge-star count and total neighbor-edge count;
- SHA-256 of the serialized template;
- deterministic repeated extraction.

Matching metrics:

- public `Match()` score in both directions;
- number of roots tried and selected root index;
- probe hash buckets and indexed edges;
- number and deterministic SHA-256 of paired minutiae;
- support-edge count;
- all score inputs and components;
- consistency between `best-score`, `best-pairing`, and public `Match()`;
- deterministic repeated matching.

Serialization metrics:

- exact template topology after round trip;
- byte-for-byte deterministic round trip;
- lossless Base64 round trip;
- a complete SourceAFIS 3.14 template fixture that must remain readable;
- rejection of malformed or internally inconsistent templates.

Identification metrics:

- score of every candidate in a small deterministic 1:N search;
- genuine candidate rank;
- separation between genuine and impostor scores.

## Development loop

For every hypothesis or improvement:

1. Start from a commit where the complete suite is green.
2. Create one branch for one hypothesis. Do not combine dependency upgrades,
   extraction changes, matcher changes, and fusion changes in one experiment.
3. Save the baseline snapshot and, for biometric experiments, the aggregate report.
4. Implement the smallest change that can test the hypothesis.
5. Run `run-experimental-tests.ps1`.
6. Run the biometric laboratory evaluation when the change can affect extraction,
   matching, threshold decisions, candidate ordering, or performance.
7. Compare baseline and experimental metrics. Record positive and negative deltas.
8. Keep the change only when its intended benefit is repeatable and regressions are
   understood. Revert the experiment otherwise.

Golden values must never be updated merely to make a failing test pass. Review the
full diagnostic diff first. Update them only when the behavior change is intentional
and supported by laboratory results.

## Biometric laboratory dataset

Do not commit real customer fingerprints to this repository. Store the laboratory
dataset outside Git with access control. Use pseudonymous subject identifiers and a
manifest containing at least:

- capture identifier;
- pseudonymous subject identifier;
- finger code;
- role (`enrollment` or `turnstile-probe`);
- reader model and capture source;
- width, height, DPI, and pixel format;
- SHA-256 of the original bytes;
- declared quality metadata, when available.

Keep two immutable partitions:

- **development set**, used to investigate and tune changes;
- **validation set**, used only to decide whether an experiment generalizes.

For every algorithm version, generate an aggregate report with:

- genuine-score distribution and low percentiles;
- impostor-score distribution and high percentiles;
- FNMR and FMR at the production threshold;
- rank-1 and rank-k identification rates;
- top-1 versus top-2 score margin;
- failures grouped by finger, device, and quality band;
- p50, p95, and p99 1:N latency with a production-sized candidate cache;
- allocation and memory measurements for cold and warm cache runs.

Raw scores and distributions matter more than a single average. An improvement that
raises the mean score but worsens the low genuine tail or the high impostor tail is
not automatically acceptable.

## Experiment report template

Record this information with every experimental commit:

```text
Hypothesis:
Baseline commit:
Experimental commit:
SourceAFIS version and dependencies:
Dataset revision/hash:
Changed files:

Metric                 Baseline    Experiment    Delta
-------------------------------------------------------
Golden tests passed
Genuine score p01
Genuine score p05
Impostor score p99.99
FNMR at threshold
FMR at threshold
Rank-1 rate
Top1-top2 margin p05
1:N latency p95
Allocations per search

Positive impacts:
Negative impacts:
Decision: keep / revise / reject
```

