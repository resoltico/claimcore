namespace ClaimCore.Application

open System
open ClaimCore.Domain

/// Unaccepted form input. This adds no fields to CaseFields and grants no write authority.
type CommandDraft =
    {
        OperationId: Guid
        CaseReference: string
        ExpectedVersion: int64
        Kind: CommandKind
        Values: (string * string) list
    }

/// Native input binding shared by downstream forms. Only IClaimsCore.Execute can accept a change.
module Drafts =
    let private command kind (values: Map<string, string>) =
        // Keys are checked exactly before this private binder is called.
        let value name = Map.find name values

        let registration () : RegistrationInput =
            {
                IncidentDate = value "incidentDate"
                IncidentNotificationDate = value "incidentNotificationDate"
                IncidentCountry = value "incidentCountry"
                ClaimantName = value "claimantName"
                InsurerName = value "insurerName"
                ClaimedAmount = value "claimedAmount"
                ClaimedCurrency = value "claimedCurrency"
            }

        match kind with
        | CommandKind.Open -> Command.Open(registration ())
        | CommandKind.AmendRegistration -> Command.AmendRegistration(registration ())
        | CommandKind.Decide ->
            Command.Decide
                {
                    PaymentDecisionDate = value "paymentDecisionDate"
                    PayableAmount = value "payableAmount"
                    PayableCurrency = value "payableCurrency"
                }
        | CommandKind.WithdrawDecision -> Command.WithdrawDecision
        | CommandKind.RecordPayment -> Command.RecordPayment(value "paymentDate")
        | CommandKind.ClearPayment -> Command.ClearPayment
        | CommandKind.Close -> Command.Close
        | CommandKind.Reopen -> Command.Reopen

    /// Binds a flat draft into the closed Domain command shape. It deliberately does not
    /// consult current state, the business clock, or durable recovery state.
    let bind (draft: CommandDraft) : Result<CommandRequest, DomainError> =
        let names = List.map fst draft.Values
        let expected = CommandDefinitions.inputFields draft.Kind

        if
            names.Length <> (Set.ofList names).Count
            || Set.ofList names <> Set.ofList expected
        then
            Error(
                DomainError.InvalidInput(
                    "fields",
                    "Supply each declared command input exactly once, with no extra fields."
                )
            )
        else
            let request =
                {
                    OperationId = draft.OperationId
                    CaseReference = draft.CaseReference
                    ExpectedVersion = draft.ExpectedVersion
                    Command = command draft.Kind (Map.ofList draft.Values)
                }

            Claim.validateRequest request |> Result.map (fun () -> request)
