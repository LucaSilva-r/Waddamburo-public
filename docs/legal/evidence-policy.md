# Evidence policy

This policy separates facts that may guide Waddamburo from material that must
remain outside the public repository. It is an engineering boundary, not legal
advice.

## Evidence categories

| Category | Examples | May inform product code? | Recording rule |
| --- | --- | --- | --- |
| Public specification | Published file specifications, public APIs, standards, upstream library documentation | Yes | Cite the document and version. Do not copy incompatible reference implementations. |
| Asset-derived observation | Container fields, dimensions, timing, or relationships measured from user-supplied data | Yes, as independently described facts | Record a non-content-reproducing description and add a synthetic regression case where possible. Never commit the input or content-reproducing output. |
| Executable/runtime observation | Visible behavior, callback ordering, timing, graphics observations, and black-box I/O | Only as behavioral requirements | Public notes may record a semantic requirement and confidence, but not addresses, code, captures, or executable layout. |
| Implementation choice | Waddamburo APIs, algorithms, architecture, constants, and error behavior | Yes | Identify it as a project decision; do not present it as an original-game fact. |

## Clean implementation boundary

Product APIs, identifiers, tests, and implementation must not contain runtime
addresses, instruction sequences, lifted pseudocode, translated executable code,
private symbols, register layouts, or structures inferred solely from executable
layout.

Runtime observation can define inputs and expected outputs, but public code must be
written independently from semantic descriptions, public specifications, and
asset-derived facts. When a claim mixes sources, its category and uncertainty must
be explicit. A hypothesis must not silently become an API contract.

Original-asset manifests, screenshots, traces, warning logs, disassemblies,
decompilations, and the private behavioral oracle remain outside this repository.
Committed summaries may contain aggregate counts, non-content-reproducing
measurements, and reproduction interfaces that use symbolic asset paths.
