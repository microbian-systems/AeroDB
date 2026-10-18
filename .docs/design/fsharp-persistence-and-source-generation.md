# F# persistence and Sable source generation

Date: 2026-10-13
Status: verified tooling limitation; direct F# support is future work, not implemented by this note.

## Key distinction

F# classes compile to normal CLR classes and are not inherently incompatible with
Sable or SurrealDB. The current obstacle is Sable's compile-time generation path,
plus the document shape expected by its generated materializers. Do not describe
this as a general inability to persist F# types.

Runtime type compatibility and source-generator compatibility are different:
runtime code sees compiled .NET types, while the existing generators inspect
C# source declarations during compilation. F# uses a separate compiler rather
than the Roslyn C# compilation pipeline.

## Current source evidence

- [AeroDBDocumentGenerator](../../src/AeroDB.SourceGenerators/AeroDBDocumentGenerator.cs)
  discovers `ClassDeclarationSyntax` nodes through Roslyn's syntax provider.
- [AeroDBEntityShimGenerator](../../src/AeroDB.SourceGenerators/AeroDBEntityShimGenerator.cs)
  uses the same C# class-declaration discovery and emits entity shims and, for
  supported shapes, typed materializer registration.
- Its generated materialization path expects a supported parameterless
  constructor and assignable properties. Required/init-only members and other
  unsupported shapes have separate fallback behavior; even C# types are not
  universally eligible for this generated path.
- [MetadataRegistry](../../src/AeroDB.Sable/Metadata/MetadataRegistry.cs) exposes
  `RegisterGeneratedMaterializer<T>`. This is an existing typed extension point,
  not proof that complete direct F# support already exists.

Normal F# records are immutable by default and need construction-based loading
rather than a create-then-assign strategy. An F# class can expose the same
constructor/property shape as a mutable C# class, but that alone does not make
the existing C# syntax generators discover its F# source. Likewise, changing a
record's mutability does not solve the compiler-pipeline limitation.

## ContractorAI workaround

ContractorAI uses the narrow C# `Contractor.AI.Membership.Data` project for
persistence contracts, source-generated metadata and schema configuration.
F# owns business policies and workflows, with explicit persistence-boundary
mapping where needed. This lets the current C# generators run without adding
runtime reflection merely to discover F# document shapes.

This is a practical tooling workaround, not a requirement imposed by the CLR.

## Future improvement candidates

Investigate a reflection-free path that can generate/register metadata and
typed readers/writers for F#-compiled document types. Candidates include explicit
typed registration or a build-time companion generator that inspects referenced
F# type symbols. Immutable records require constructor-based materialization;
F# options, unions and collections require explicit supported serialization rules.

Before replacing the workaround, prove parity for save/load/query, IDs, naming,
null/absence, audit fields, optimistic concurrency, tenant routing and the actual
transport/serialization paths. Materializer registration alone is insufficient.
Any implementation requires a separately reviewed plan and test-first coverage.
This note changes no code, dependencies, schemas or runtime behavior.

## Official background

- [Roslyn extensibility](https://learn.microsoft.com/visualstudio/extensibility/dotnet-compiler-platform-roslyn-extensibility)
- [F# records and their differences from classes](https://learn.microsoft.com/dotnet/fsharp/language-reference/records#differences-between-records-and-classes)
