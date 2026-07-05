# Samples

Sample applications demonstrating AeroDB -- a SurrealDB-backed document database and event store with Marten API compatibility.

## Attributions

Several sample projects in this directory originated from the [Marten](https://github.com/JasperFx/marten) project. These samples have been ported to use AeroDB as a drop-in replacement.

### Marten-originated samples

| Sample | Original Source | Description |
|--------|----------------|-------------|
| `EventSourcingIntro/` | Marten samples/EventSourcingIntro | Warehouse inventory event sourcing with single-stream projection |
| `DocSamples/` | Marten samples/DocSamples | CQRS/ES documentation samples (quest party, projections, snapshots) |
| `MinimalAPI/` | Marten samples/MinimalAPI | ASP.NET Core minimal API with Marten document/event store |
| `AspireHeadlessTripService/` | Marten src/AspireHeadlessTripService | OpenTelemetry daemon demo with trip projections |
| `AspireHost/` | Marten src/AspireHost | .NET Aspire orchestrator (no Marten dependency) |
| `Helpdesk/` | Marten samples/Helpdesk | Full CQRS/ES web API with incidents domain, Kafka, SignalR |

### AeroDB-native samples

| Sample | Description |
|--------|-------------|
| `CryptoTrader/` | Cryptocurrency trading sample |
| `WikipediaSearch/` | Wikipedia search demo with ONNX embeddings and vector search |

## License

The Marten-originated samples are derived from [Marten](https://github.com/JasperFx/marten) which is licensed under the MIT License:

```
Copyright (c) Jeremy D. Miller, Babu Annamalai, Oskar Dudycz,
Joona-Pekka Kokko and Contributors.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The ported versions in this repository are also provided under the same MIT License terms, with modifications to use AeroDB instead of Marten.

## Porting Status

See [.docs/plan/marten-samples-port.md](../.docs/plan/marten-samples-port.md) for the full porting plan and progress tracking.

| Sample | Status |
|--------|--------|
| EventSourcingIntro | Pending |
| DocSamples | Pending |
| MinimalAPI | Pending |
| AspireHeadlessTripService | Pending |
| AspireHost | N/A (orchestrator only) |
| Helpdesk.Api | Pending |
| Helpdesk.Api.Tests | Pending |
