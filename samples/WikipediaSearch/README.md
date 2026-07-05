# AeroDB Wikipedia Search Demo

Exercises every search capability of **AeroDB** (SurrealDB document store) using real Wikipedia articles with semantic embeddings from the **all-MiniLM-L6-v2** ONNX model.

## Quick Start

```powershell
# 1. Download the ONNX model (one-time, ~92 MB)
.\download-model.ps1

# 2. Run the demo (requires internet for Wikipedia fetch)
dotnet run
```

That's it. The script downloads the model, `dotnet run` fetches 20 random Wikipedia articles, generates real embeddings, and runs all 7 search demos.

## Command-line options

| Command | Mode | Details |
|---|---|---|
| `dotnet run` | **Server** (default) | Connects to a local SurrealDB at `ws://localhost:8000/rpc` with no auth. If the server isn't reachable, **automatically falls back** to embedded mode. |
| `dotnet run embedded` | **Embedded** | Uses the SurrealKv embedded engine (RocksDB in a temp directory). No server needed. |

**Server mode** supports all 3 vector index types (HNSW + DiskANN) when running against SurrealDB 3.1+. **Embedded mode** supports HNSW only (DiskANN requires 3.1+, MTREE was removed in 3.x).

## What you need

| Requirement | Details |
|---|---|
| .NET SDK | 10.0+ |
| Internet | To fetch Wikipedia articles at runtime |
| ~92 MB disk | ONNX model files (downloaded once) |

## What it demonstrates

| # | Search Type | AeroDB API | Index |
|---|---|---|---|
| 1 | **Full-Text Search** | `MatchTextAsync()` single + multi-field weighted | FullText (English + Simple analyzers) |
| 2 | **HNSW Vector** | `MatchKnnAsync()` | HNSW (Cosine distance) |
| 2 | **HNSW Vector** | `MatchKnnAsync()` | HNSW (Cosine distance) |
| 3 | **DiskANN Vector** | `MatchKnnAsync()` (SurrealDB Server 3.1+, not embedded) | DiskANN (Cosine, F32) |
| 4 | **Hybrid (FTS + Vector)** | `HybridSearchAsync()` | FTS + HNSW via RRF fusion |
| 5 | **Plain / Prefix / Phrase** | `RawQueryAsync()` | FullText |
| 6 | **Raw SurrealQL** | `RawQueryAsync()` | All indexes |

## How it works

```
[Init]     Load ONNX model → 384-dim embeddings
[Fetch]    20x parallel Wikipedia fetches (AngleSharp)
[Embed]    Batch ONNX inference → float[384] per article
[Store]    All articles in SurrealKV (RocksDB-backed)
[Demos]    Run all 7 search types sequentially
[Cleanup]  Delete temp DB
```

## Project structure

```
samples/WikipediaSearch/
├── download-model.ps1           # One-click model download
├── WikipediaSearch.csproj
├── Program.cs                   # Orchestrator
├── WikipediaFetcher.cs          # HttpClient + AngleSharp parser
├── EmbeddingGenerator.cs        # ONNX Runtime + BERT WordPiece tokenizer
├── SearchDemos.cs               # All 7 AeroDB search demos
├── README.md                    # You are here
└── models/
    └── all-MiniLM-L6-v2/        # Downloaded by the script
        ├── model.onnx           (91 MB)
        ├── vocab.txt            (232 KB)
        ├── config.json          (642 B)
        └── tokenizer.json       (712 KB)
```

## Manual model download (alternative)

If you can't run the PowerShell script, download these 4 files into `models/all-MiniLM-L6-v2/`:

| File | Size | URL |
|---|---|---|
| `model.onnx` | 91 MB | https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx/resolve/main/model.onnx |
| `vocab.txt` | 232 KB | https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx/resolve/main/vocab.txt |
| `config.json` | 642 B | https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx/resolve/main/config.json |
| `tokenizer.json` | 712 KB | https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx/resolve/main/tokenizer.json |

The final layout must be:

```
samples/WikipediaSearch/models/all-MiniLM-L6-v2/
├── model.onnx
├── vocab.txt
├── config.json
└── tokenizer.json
```

## Expected output

```
[Init] Loading ONNX model from .../models/all-MiniLM-L6-v2
  [Embedding] Model loaded — 384-dim, max_length=256
[Init] Model ready — 384-dim embeddings

[Fetch] Retrieving 20 random Wikipedia articles in parallel...

  - "Quantum Computing" (4523 chars)
  - "Battle of Hastings" (3145 chars)
  - "Mona Lisa" (2834 chars)
  ... and 17 more

[Fetch] Got 20 unique articles in 2341ms

=== Search Demos (20 articles) ===

═══ Full-Text Search ═══
  Query: "Quantum"
  [MatchTextAsync (Content)] 3 result(s):
    - Quantum Computing
    - Quantum Mechanics
    - Quantum Entanglement

  [MatchTextAsync (Title x3 + Content)] 3 result(s):
    - Quantum Computing
    - Quantum Mechanics
    - Quantum Entanglement

═══ HNSW Vector Search (Cosine) ═══
  Query vector: embedding of "Quantum Computing"
  [MatchKnnAsync (HNSW)] 5 result(s):
    - Quantum Computing
    - Quantum Mechanics
    - Computer Science
    ...

═══ All demos complete ═══
Total time: 8743ms
```

## About the embedding model

**all-MiniLM-L6-v2** is a sentence-transformer model that maps text to a 384-dimensional vector space. It was trained on 1B+ sentence pairs for semantic similarity. The ONNX conversion is from [nsense/all-MiniLM-L6-v2-onnx](https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx) (MIT license).

- Input: text up to 256 tokens
- Output: `float[384]` normalized embedding
- Inference: ONNX Runtime (CPU, +DirectML on Windows)

## Dependencies

| Package | Purpose |
|---|---|
| `AeroDB` | Document store + search API |
| `AngleSharp` | HTML parsing (Wikipedia) |
| `Microsoft.ML.OnnxRuntime` | ONNX model inference |
| `SurrealDb.Embedded.SurrealKv` | Disk-based SurrealDB |

## Credits

Built with [AeroDB](https://github.com/microbian-systems/AeroDB). Wikipedia content via [Wikipedia](https://wikipedia.org) (CC BY-SA 4.0). Embedding model [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) by HuggingFace.
