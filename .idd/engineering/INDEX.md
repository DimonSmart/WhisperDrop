# Engineering Rules Index

This index is a compact discovery projection for current Engineering Rules. Full rule documents are normative.

The `Rule` column contains stable `ENG-NNNN` identifiers only. Resolve each ID to exactly one current `.idd/engineering/ENG-NNNN.rule-*.md` file.

Next ID: ENG-0007

| Rule | Applicability | Applies when | Summary |
| --- | --- | --- | --- |
| ENG-0001 | Always | Every implementation task | Use the selected .NET, Uno, Skia, DI, and hosting foundation while keeping the application simple. |
| ENG-0002 | Conditional | Local recognition, model management, or recognition-runtime packaging | Use the pinned transcribe.cpp C API and Parakeet model with app-local native runtimes and serialized model use. |
| ENG-0003 | Conditional | Settings persistence, project structure, or local model integration verification | Use atomic JSON settings, immutable recognition snapshots, and explicitly controlled model integration checks. |
| ENG-0004 | Conditional | CI/CD, releases, versioning, About, or application updates | Keep release identity, artifact naming, Homebrew metadata, and updater checks coordinated around Git-tagged releases. |
| ENG-0005 | Conditional | AI transcript post-processing or OpenAI-compatible integration | Use Agent Framework Chat Completions with deterministic chunking, typed validation, raw-result preservation, stateless chunks, and secret/content-safe handling. |
| ENG-0006 | Conditional | Media input, decoding, normalization, or media runtime packaging | Decode media in-process with pinned bundled FFmpeg, normalize once to 16 kHz mono PCM WAV, and keep source decoding separate from native recognition. |
