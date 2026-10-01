# IDD Intent Index

This index helps humans and Coding Agents find relevant current intent documents.
It is not the source of truth.

Current `IDD-NNNN` documents directly under `.idd/intent/` contain normative
product intent, ADRs, or active spikes.

`GLOSSARY.md`, when present, is an optional unnumbered vocabulary support file
and is not listed in this index.

Git history is the source for deleted or previous document versions.

For shared or cross-cutting product intent, use `Area`, `Notes`, or equivalent
discovery metadata to make the scope obvious to planners. Existing projects do
not need a schema migration; keep their current INDEX shape when it already
communicates this information.

Implementation-only durable constraints belong in the optional
`.idd/engineering/` layer, not in this index.

## Current documents

The `Document` column contains stable `IDD-NNNN` identifiers only. Do not put
filenames, file paths, or Markdown links in this column. Resolve an identifier to
the unique current `.idd/intent/IDD-NNNN.*.md` file when the document must be
opened.

| Document | Role | Area | Notes | Replaces |
| --- | --- | --- | --- | --- |
| IDD-0001 | Spec | Product overview and privacy | Local desktop transcription, supported platforms, privacy, distribution, and session-data boundaries. | — |
| IDD-0002 | Spec | Transcription workspace | File queue, ordering, sequential transcription, language display, previews, and clipboard results. | — |
| IDD-0003 | Spec | Recognition and models | Recognition choices on Transcribe; local model storage, inventory, sizes, explicit downloads, deletion, availability, and persistent settings. | — |
| IDD-0004 | Spec | Visual system | Compact dark desktop presentation, semantic visual resources, Transcribe/Models navigation, queue hierarchy, and model-management styling. | — |
| IDD-0005 | Spec | Release distribution | CI, tag-driven GitHub Releases, Windows/macOS artifacts, checksums, and Homebrew Cask publication. | — |
| IDD-0006 | Spec | About and application updates | Version display, GitHub update checks, release navigation, and safe Homebrew-based macOS self-update. | — |
| IDD-0007 | Spec | AI transcript post-processing | Optional Agent Framework correction, raw-result preservation, OpenAI-compatible endpoints, chunking, structured validation, and AI failure semantics. | — |
