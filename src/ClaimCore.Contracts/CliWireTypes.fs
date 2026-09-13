namespace ClaimCore.Contracts

/// Fully encoded CLI-v3 response. The payload is canonical UTF-8 JSON followed by one LF.
[<NoEquality; NoComparison>]
type CliWireResponse = { ExitCode: int; Bytes: byte array }
