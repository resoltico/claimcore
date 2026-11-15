namespace ClaimCore.Postgres

open System
open ClaimCore.Application.Contracts

/// Source-specific owner refusal; its public value carries no exception message or submitted value.
[<Sealed>]
type OperatorFaultException(fault: OperatorFault) =
    inherit InvalidOperationException("The owner operation was refused.")
    member _.Fault = fault

/// The baseline exists, but its immutable installation tuple cannot be admitted.
type internal InstallationConfigurationException() =
    inherit InvalidOperationException("The installation configuration is invalid.")

/// Rebuild is an owner operation, but it still requires explicit register-write maintenance.
type MaintenanceRequiredException() =
    inherit InvalidOperationException("Enter register maintenance before rebuilding projections.")
