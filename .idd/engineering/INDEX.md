# Engineering Rules Index

This index is a compact discovery projection for current Engineering Rules. Full rule documents are normative.

The `Rule` column contains stable `ENG-NNNN` identifiers only. Resolve each ID to exactly one current `.idd/engineering/ENG-NNNN.rule-*.md` file.

Next ID: ENG-0006

| Rule | Applicability | Applies when | Summary |
| --- | --- | --- | --- |
| ENG-0001 | Always | Every implementation task | Use the selected .NET, Uno, Skia, DI, and hosting foundation while keeping the application simple. |
| ENG-0002 | Conditional | Local transcription, model management, or recognition execution | Use Whisper.net and a stable bundled runtime with selected-model factory reuse. |
| ENG-0003 | Conditional | Settings persistence, project structure, or Whisper integration verification | Use focused JSON settings persistence, minimal project structure, and a separate local-model integration check. |
| ENG-0004 | Conditional | CI/CD, releases, versioning, About, or application updates | Treat Git tags as release versions, keep release packaging verified, and isolate GitHub/Homebrew/process update semantics behind services. |
| ENG-0005 | Conditional | AI transcript post-processing or OpenAI-compatible integration | Use Agent Framework Chat Completions with deterministic chunking, typed validation, raw-result preservation, stateless chunks, and secret/content-safe handling. |
