namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Json

/// Adapt already authorized outcomes to human messages; never load or decide a second result.
module HumanReplies =
    let private arguments values =
        values
        |> List.map (fun (name, value) ->
            name,
            match value with
            | JsonValue.Integer value -> PresentedValue.Count value
            | JsonValue.String value -> PresentedValue.Text value
            | _ -> invalidOp "Diagnostic arguments are declared scalar values.")
        |> Map.ofList

    let private message code values =
        {
            PresentedMessage.MessageId = DiagnosticCode.key code
            Arguments = arguments values
        }

    let private refusalAdvice refusal =
        let code, values = DiagnosticCodecs.describe refusal
        let message = message code values

        let parameters =
            match refusal with
            | Refusal.Access(AccessRefusal.NotAuthorized required) ->
                Map.add
                    "required"
                    (PresentedValue.Codes(
                        Vocabulary.name Role.vocabulary,
                        Authority.rolesFor required |> List.map Role.token
                    ))
                    message.Arguments
            | Refusal.Case(CaseRefusal.NotOffered state) ->
                Map.add "state" (PresentedValue.State state) message.Arguments
            | Refusal.Reminder(ReminderRuleRefusal.NotOffered state) ->
                Map.add
                    "state"
                    (PresentedValue.Code(
                        Vocabulary.name ReminderState.vocabulary,
                        ReminderState.token state
                    ))
                    message.Arguments
            | Refusal.OperationClockUnavailable(cutoff, observed) ->
                Map.ofList
                    [
                        "expiredBefore",
                        PresentedValue.Text(
                            ClaimCore.RecordFormat.CheckpointRecord.formatFrontier cutoff
                        )
                        "observedAt", PresentedValue.Instant observed
                    ]
            | Refusal.OperationIdOutOfWindow(issued, observed) ->
                Map.ofList
                    [
                        "issuedAt", PresentedValue.Instant issued
                        "observedAt", PresentedValue.Instant observed
                    ]
            | _ -> message.Arguments

        {
            HumanAdvice.Message = { message with Arguments = parameters }
            Action = Refusals.action refusal
        }

    let private inputIssue (issue: InputIssue) =
        let target = TaskInputIssue.ofDomain issue
        let code, parameters = DiagnosticCodecs.describeIssue issue

        {
            HumanInputIssue.Input = target.Input
            Row = target.Row
            Message = message code parameters
            Related =
                issue.RelatedDate
                |> Option.map (fun related ->
                    ScreenEntries.value
                        "relatedDate"
                        (FieldPresentation.labelId related.Field)
                        Prominence.Secondary
                        (PresentedValue.Date related.Value))
        }

    let rejection refusal =
        let advice = refusalAdvice refusal

        match refusal with
        | Refusal.InvalidInput issues ->
            HumanRejection.Input(advice, InputIssues.toList issues |> List.map inputIssue)
        | Refusal.RevisionConflict conflict ->
            let document =
                match conflict.Changes with
                | Some(ConflictChanges.Page page) ->
                    {
                        Screen = TimelineScreen.rows page.Entries
                        Context = InteractionContext.Continuation page.NextCursor
                        Basis = ObservationBasis.Snapshot page.Basis
                        Disclosure =
                            match page.Disclosure with
                            | HistoryDisclosure.Full -> HumanDisclosure.Full
                            | HistoryDisclosure.Summary -> HumanDisclosure.Summary
                            | HistoryDisclosure.Erased -> HumanDisclosure.Erased
                    }
                | Some ConflictChanges.Withheld
                | None -> HumanDocument.empty HumanDisclosure.Withheld

            HumanRejection.Conflict(advice, conflict.ActualRevision, document)
        | _ -> HumanRejection.Refused advice

    let failure fault =
        {
            HumanAdvice.Message =
                {
                    MessageId = DiagnosticCode.key (CoreFault.token fault)
                    Arguments = Map.empty
                }
            Action = fault.Action
        }

    let read describe outcome =
        {
            HumanReadReply.Outcome =
                match outcome with
                | Outcome.Succeeded value -> HumanReadOutcome.Succeeded(describe value)
                | Outcome.Rejected refusal -> HumanReadOutcome.Rejected(rejection refusal)
                | Outcome.Failed fault -> HumanReadOutcome.Failed(failure fault)
        }

    let preview input (reply: PreviewReply) =
        {
            HumanOperationReply.OperationId = reply.OperationId
            Outcome =
                match reply.Outcome with
                | Outcome.Succeeded(Previewed.Reviewed review) ->
                    HumanPreviewResult.Reviewed(HumanReviewDocument.ofCore input review)
                | Outcome.Succeeded(Previewed.AlreadyAccepted receipt) ->
                    HumanPreviewResult.AlreadyAccepted(HumanReceiptDocument.ofCore receipt)
                | Outcome.Rejected value -> HumanPreviewResult.Rejected(rejection value)
                | Outcome.Failed value -> HumanPreviewResult.Failed(failure value)
        }

    let execute (reply: ExecuteReply) =
        {
            HumanOperationReply.OperationId = reply.OperationId
            Outcome =
                match reply.Outcome with
                | ExecuteResult.Accepted receipt ->
                    HumanExecuteResult.Accepted(HumanReceiptDocument.ofCore receipt)
                | ExecuteResult.AlreadyAccepted receipt ->
                    HumanExecuteResult.AlreadyAccepted(HumanReceiptDocument.ofCore receipt)
                | ExecuteResult.Rejected value -> HumanExecuteResult.Rejected(rejection value)
                | ExecuteResult.Failed value -> HumanExecuteResult.Failed(failure value)
                | ExecuteResult.OutcomeUnknown -> HumanExecuteResult.OutcomeUnknown
        }

    let lookup (reply: OperationReply<OperationLookupResult>) =
        let document screen =
            { HumanDocument.empty HumanDisclosure.None with
                Screen = screen
            }

        {
            HumanOperationReply.OperationId = reply.OperationId
            Outcome =
                match reply.Outcome with
                | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, own)) ->
                    HumanLookupResult.Accepted(HumanReceiptDocument.ofCore receipt, own)
                | Outcome.Succeeded(OperationLookupResult.Pending finalAt) ->
                    HumanLookupResult.Pending(finalAt, document (OperationScreen.pending finalAt))
                | Outcome.Succeeded OperationLookupResult.NotAccepted ->
                    HumanLookupResult.NotAccepted(document (OperationScreen.notAccepted ()))
                | Outcome.Rejected value -> HumanLookupResult.Rejected(rejection value)
                | Outcome.Failed value -> HumanLookupResult.Failed(failure value)
        }
