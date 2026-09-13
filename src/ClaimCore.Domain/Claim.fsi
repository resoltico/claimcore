namespace ClaimCore.Domain

open System

/// Opaque accepted state. Public read models cannot construct this representation.
type Claim

module Claim =
    val validateReference: reference: string -> Result<unit, DomainError>
    val validateRequest: request: CommandRequest -> Result<unit, DomainError>
    val view: claim: Claim -> CaseView
    val restore: snapshot: CaseView -> Result<Claim, DomainError>

    val decide:
        today: DateOnly ->
        request: CommandRequest ->
        current: Claim option ->
            Result<Claim, DomainError>

    val availableCommands: claim: Claim -> string list
