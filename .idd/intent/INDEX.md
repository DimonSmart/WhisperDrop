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
| IDD-0001 | Spec | Product overview and privacy | ParakeetDrop identity, local offline transcription, bundled media/native runtimes, supported platforms, privacy, and session-data boundaries. | — |
| IDD-0002 | Spec | Transcription workspace | Audio/video input, media preparation, ordered queue, non-streaming recognition, language display, complete results, and clipboard behavior. | — |
| IDD-0003 | Spec | Recognition and models | Single Parakeet Ultra model, explicit verified download, local storage, device selection, availability, and new-install settings. | — |
| IDD-0004 | Spec | Visual system | Compact dark desktop presentation, semantic resources, Transcribe/Models/AI navigation, queue hierarchy, and model-management styling. | — |
| IDD-0005 | Spec | Release distribution | Tag-driven releases, ParakeetDrop archives and Cask, bundled media/native runtimes, checksums, and package verification. | — |
| IDD-0006 | Spec | About and application updates | ParakeetDrop version display, GitHub update checks, release navigation, and safe Homebrew-based macOS self-update. | — |
| IDD-0007 | Spec | AI transcript post-processing | Optional Agent Framework correction, immutable raw result, OpenAI-compatible endpoints, chunking, structured validation, and AI failure semantics. | — |
| IDD-0008 | ADR | Parakeet Ultra native runtime | Decision to use one pinned Parakeet Ultra GGUF with the transcribe.cpp C API, app-local native backends, and verified platform behavior. | — |
| IDD-0009 | Spec | Push-to-Talk dictation | Windows background hotkey, tray, mandatory recording overlay, shared history, verified field identity, and fail-closed text insertion; macOS next. | — |
