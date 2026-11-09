namespace ClaimCore.Postgres

open System
open ClaimCore.Application.Contracts

/// Source-specific owner refusal; its public value carries no exception message or submitted value.
[<Sealed>]
type OperatorFaultException(fault: OperatorFault) =
    inherit InvalidOperationException("The owner operation was refused.")
    member _.Fault = fault
