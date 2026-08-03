; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ADB001 | AeroDB.Schema | Error | Reports computed properties that are serialized but excluded from schema generation
ADB100 | AeroDB.Security | Error | Prevents server-side queries over encrypted fields
ADB101 | AeroDB.Security | Error | Rejects unsupported encrypted field types
ADB102 | AeroDB.Security | Error | Requires blind indexes to target encrypted string fields
ADB106 | AeroDB.Security | Error | Rejects undefined encryption attribute settings
ADB107 | AeroDB.Security | Error | Prevents document identity fields from being encrypted
