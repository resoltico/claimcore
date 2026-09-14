namespace ClaimCore.Application

open System
open ClaimCore.Domain

/// One explicitly tagged group received from a form transport. It is not a Domain correction until
/// `Drafts.bind` has checked every declared group and replacement field exactly.
[<RequireQualifiedAccess>]
type CorrectionDraftAction =
    | Keep
    | Clear
    | Replace of (string * string) list

/// Transport/form command shape. Native callers never receive this type: they call the closed
/// `IClaimsCore` boundary with Domain's `CommandRequest` after this one pure binder has run.
[<RequireQualifiedAccess>]
type DraftCommand =
    | Flat of kind: CommandKind * values: (string * string) list
    | Correction of
        registration: CorrectionDraftAction *
        decision: CorrectionDraftAction *
        payment: CorrectionDraftAction

/// Unaccepted form input. This adds no fields to CaseFields and grants no write authority.
type CommandDraft =
    {
        OperationId: Guid
        CaseReference: string
        ExpectedVersion: int64
        Command: DraftCommand
    }

/// Native input binding shared by downstream forms. Only IClaimsCore.Execute can accept a change.
module Drafts =
    let private flatCommand kind (values: Map<string, string>) =
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
        | CommandKind.CorrectCase ->
            invalidArg (nameof kind) "Grouped correction input cannot use the flat draft binder."
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

    let private exactValues field expected (values: (string * string) list) =
        let names = values |> List.map fst

        if
            names.Length <> (Set.ofList names).Count
            || Set.ofList names <> Set.ofList expected
        then
            Error(
                DomainError.InvalidInput(
                    field,
                    "Supply each declared command input exactly once, with no extra fields."
                )
            )
        else
            Ok(Map.ofList values)

    let private replace fields input action =
        match action with
        | CorrectionDraftAction.Replace values ->
            exactValues input (fields |> List.map _.FieldName) values
        | _ -> Error(DomainError.InvalidInput(input, "Use REPLACE with every declared field."))

    let private correctionGroups () =
        match (CommandDefinitions.forKind CommandKind.CorrectCase).Inputs with
        | CommandInputShape.CorrectionGroups groups -> groups
        | CommandInputShape.Fields _ -> invalidOp "Correction command must declare groups."

    let private correctionGroup name =
        correctionGroups () |> List.find (fun definition -> definition.Name = name)

    let private registrationCorrection action =
        match action with
        | CorrectionDraftAction.Keep -> Ok RegistrationCorrection.Keep
        | CorrectionDraftAction.Replace _ ->
            replace (correctionGroup "registration").ReplaceFields "registration" action
            |> Result.map (fun values ->
                RegistrationCorrection.Replace
                    {
                        IncidentDate = Map.find "incidentDate" values
                        IncidentNotificationDate = Map.find "incidentNotificationDate" values
                        IncidentCountry = Map.find "incidentCountry" values
                        ClaimantName = Map.find "claimantName" values
                        InsurerName = Map.find "insurerName" values
                        ClaimedAmount = Map.find "claimedAmount" values
                        ClaimedCurrency = Map.find "claimedCurrency" values
                    })
        | CorrectionDraftAction.Clear ->
            Error(DomainError.InvalidInput("registration", "Registration cannot be cleared."))

    let private decisionCorrection action =
        match action with
        | CorrectionDraftAction.Keep -> Ok DecisionCorrection.Keep
        | CorrectionDraftAction.Clear -> Ok DecisionCorrection.Clear
        | CorrectionDraftAction.Replace _ ->
            replace (correctionGroup "decision").ReplaceFields "decision" action
            |> Result.map (fun values ->
                DecisionCorrection.Replace
                    {
                        PaymentDecisionDate = Map.find "paymentDecisionDate" values
                        PayableAmount = Map.find "payableAmount" values
                        PayableCurrency = Map.find "payableCurrency" values
                    })

    let private paymentCorrection action =
        match action with
        | CorrectionDraftAction.Keep -> Ok PaymentCorrection.Keep
        | CorrectionDraftAction.Clear -> Ok PaymentCorrection.Clear
        | CorrectionDraftAction.Replace _ ->
            replace (correctionGroup "payment").ReplaceFields "payment" action
            |> Result.map (fun values -> PaymentCorrection.Replace(Map.find "paymentDate" values))

    let private correctionCommand registration decision payment =
        let registrationCommand = registrationCorrection registration
        let decisionCommand = decisionCorrection decision
        let paymentCommand = paymentCorrection payment

        match registrationCommand, decisionCommand, paymentCommand with
        | Ok registrationValue, Ok decisionValue, Ok paymentValue ->
            Ok(
                Command.CorrectCase
                    {
                        Registration = registrationValue
                        Decision = decisionValue
                        Payment = paymentValue
                    }
            )
        | Error error, _, _
        | _, Error error, _
        | _, _, Error error -> Error error

    /// Binds form/transport input into the closed Domain command shape. It deliberately does not
    /// consult current state, the business clock, or durable recovery state.
    let bind (draft: CommandDraft) : Result<CommandRequest, DomainError> =
        let command =
            match draft.Command with
            | DraftCommand.Flat(kind, values) ->
                match CommandDefinitions.flatInputFields kind with
                | Error message -> Error(DomainError.InvalidInput("command", message))
                | Ok fields ->
                    exactValues "fields" (fields |> List.map _.FieldName) values
                    |> Result.map (flatCommand kind)
            | DraftCommand.Correction(registration, decision, payment) ->
                correctionCommand registration decision payment

        command
        |> Result.bind (fun bound ->
            let request: CommandRequest =
                {
                    OperationId = draft.OperationId
                    CaseReference = draft.CaseReference
                    ExpectedVersion = draft.ExpectedVersion
                    Command = bound
                }

            Claim.validateRequest request |> Result.map (fun () -> request))

    /// Adapter-only binding preserves the public typed refusal vocabulary without exposing a store.
    let bindForEndpoint (draft: CommandDraft) : Result<CommandRequest, Rejection> =
        bind draft |> Result.mapError CoreConversions.rejection
