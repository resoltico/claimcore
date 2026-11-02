namespace ClaimCore.Protocol

open ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.Json


module ReplyCodecs =
    open Codec

    let private data codec = record id |> field "data" id codec

    let readReply name (codec: Codec<'T>) : Codec<Outcome<'T>> =
        let outcome =
            tagged
                (name + ".Outcome")
                [
                    variant
                        "SUCCEEDED"
                        Outcome.Succeeded
                        (function
                        | Outcome.Succeeded value -> Some value
                        | _ -> None)
                        (data codec)
                    variant
                        "REJECTED"
                        Outcome.Rejected
                        (function
                        | Outcome.Rejected value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.diagnostic)
                    variant
                        "FAILED"
                        Outcome.Failed
                        (function
                        | Outcome.Failed value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.fault)
                ]

        record id |> field "outcome" id outcome |> build |> named name

    let operationReply name outcome =
        (record (fun operationId value ->
            {
                OperationReply.OperationId = operationId
                Outcome = value
            })
        : Fields<OperationReply<'value>, _>)
        |> field "operationId" _.OperationId uuid
        |> flatten _.Outcome (record id |> field "outcome" id outcome)
        |> build
        |> named name

    let observation =
        let outcome =
            tagged
                "ObservationResult"
                [
                    variant
                        "SUCCEEDED"
                        Outcome.Succeeded
                        (function
                        | Outcome.Succeeded value -> Some value
                        | _ -> None)
                        (data ReceiptCodecs.receipt)
                    variant
                        "REJECTED"
                        Outcome.Rejected
                        (function
                        | Outcome.Rejected value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.diagnostic)
                    variant
                        "FAILED"
                        Outcome.Failed
                        (function
                        | Outcome.Failed value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.fault)
                ]

        operationReply "ObservationReply" outcome

    let private capabilityAccess =
        (record (fun capability direct inherited effective ->
            {
                Capability = capability
                DirectRoles = direct
                InstallationRoles = inherited
                Effective = effective
            })
        : Fields<CapabilityAccess, _>)
        |> field "capability" _.Capability (VocabularyCodec.create Capability.vocabulary)
        |> field
            "directRoles"
            _.DirectRoles
            (list Role.all.Length (VocabularyCodec.create Role.vocabulary))
        |> field
            "installationRoles"
            _.InstallationRoles
            (list Role.all.Length (VocabularyCodec.create Role.vocabulary))
        |> field "effective" _.Effective boolean
        |> build
        |> named "CapabilityAccess"

    let authorityState =
        (record (fun enabled grants access ->
            {
                Enabled = enabled
                Grants = grants
                Access = access
            })
        : Fields<AuthorityState, _>)
        |> field "enabled" _.Enabled boolean
        |> field "grants" _.Grants (list 1 ResponseCodecs.authorityGrant)
        |> field "access" _.Access (list Capability.all.Length capabilityAccess)
        |> build
        |> named "AuthorityState"

    let private authorityReviewFields =
        (record (fun target scope before after ->
            {
                Target = target
                AccessScope = scope
                Before = before
                After = after
            })
        : Fields<AuthorityReview, _>)
        |> field "target" _.Target (nullable ResponseCodecs.actorSummary)
        |> field "accessScope" _.AccessScope ResponseCodecs.grantScope
        |> field "before" _.Before authorityState
        |> field "after" _.After authorityState

    let private workReviewFields =
        (record (fun reference before proposed context ->
            {
                WorkReview.Reference = reference
                Before = before
                Proposed = proposed
                Context = context
            })
        : Fields<WorkReview, _>)
        |> field "caseReference" _.Reference ResponseScalars.caseReference
        |> field "before" _.Before WorkCodecs.workSnapshot
        |> field "proposed" _.Proposed WorkCodecs.workSnapshot
        |> field "context" _.Context ResponseCodecs.runtimeContext

    let private reminderReviewFields =
        (record (fun parent reference before effects context ->
            {
                ReminderReview.CaseId = parent
                Reference = reference
                Before = before
                Effects = effects
                Context = context
            })
        : Fields<ReminderReview, _>)
        |> field "caseId" _.CaseId ResponseScalars.caseId
        |> field "caseReference" _.Reference ResponseScalars.caseReference
        |> field "before" _.Before (nullable WorkCodecs.reminder)
        |> field "effects" _.Effects WorkCodecs.effects
        |> field "context" _.Context ResponseCodecs.runtimeContext

    let review =
        let subject =
            tagged
                "ReviewSubject"
                [
                    variant
                        "WORK"
                        OperationReview.Work
                        (function
                        | OperationReview.Work value -> Some value
                        | _ -> None)
                        workReviewFields
                    variant
                        "REMINDER"
                        OperationReview.Reminder
                        (function
                        | OperationReview.Reminder value -> Some value
                        | _ -> None)
                        reminderReviewFields
                    variant
                        "CASE"
                        OperationReview.Case
                        (function
                        | OperationReview.Case value -> Some value
                        | _ -> None)
                        ResponseCodecs.reviewFields
                    variant
                        "AUTHORITY"
                        OperationReview.Authority
                        (function
                        | OperationReview.Authority value -> Some value
                        | _ -> None)
                        authorityReviewFields
                ]

        record id |> field "subject" id subject |> build |> named "OperationReview"

    let private reviewed = record id |> field "review" id review |> build

    let private previewResult =
        tagged
            "PreviewResult"
            [
                variant
                    "REVIEWED"
                    (Previewed.Reviewed >> Outcome.Succeeded)
                    (function
                    | Outcome.Succeeded(Previewed.Reviewed value) -> Some value
                    | _ -> None)
                    (data reviewed)
                variant
                    "ALREADY_ACCEPTED"
                    (Previewed.AlreadyAccepted >> Outcome.Succeeded)
                    (function
                    | Outcome.Succeeded(Previewed.AlreadyAccepted value) -> Some value
                    | _ -> None)
                    (data ReceiptCodecs.receipt)
                variant
                    "REJECTED"
                    Outcome.Rejected
                    (function
                    | Outcome.Rejected value -> Some value
                    | _ -> None)
                    (data DiagnosticCodecs.diagnostic)
                variant
                    "FAILED"
                    Outcome.Failed
                    (function
                    | Outcome.Failed value -> Some value
                    | _ -> None)
                    (data DiagnosticCodecs.fault)
            ]

    let preview =
        (record (fun id outcome ->
            {
                PreviewReply.OperationId = id
                Outcome = outcome
            })
        : Fields<PreviewReply, _>)
        |> field "operationId" _.OperationId Codec.uuid
        |> field "outcome" _.Outcome previewResult
        |> build
        |> named "PreviewReply"

    let private executeResult =
        tagged
            "ExecuteResult"
            [
                variant
                    "ACCEPTED"
                    ExecuteResult.Accepted
                    (function
                    | ExecuteResult.Accepted value -> Some value
                    | _ -> None)
                    (data ReceiptCodecs.receipt)
                variant
                    "ALREADY_ACCEPTED"
                    ExecuteResult.AlreadyAccepted
                    (function
                    | ExecuteResult.AlreadyAccepted value -> Some value
                    | _ -> None)
                    (data ReceiptCodecs.receipt)
                variant
                    "REJECTED"
                    ExecuteResult.Rejected
                    (function
                    | ExecuteResult.Rejected value -> Some value
                    | _ -> None)
                    (data DiagnosticCodecs.diagnostic)
                variant
                    "FAILED"
                    ExecuteResult.Failed
                    (function
                    | ExecuteResult.Failed value -> Some value
                    | _ -> None)
                    (data DiagnosticCodecs.fault)
                variant
                    "OUTCOME_UNKNOWN"
                    (fun () -> ExecuteResult.OutcomeUnknown)
                    (function
                    | ExecuteResult.OutcomeUnknown -> Some()
                    | _ -> None)
                    (data nullValue)
            ]

    let execute =
        (record (fun id outcome ->
            {
                ExecuteReply.OperationId = id
                Outcome = outcome
            })
        : Fields<ExecuteReply, _>)
        |> field "operationId" _.OperationId Codec.uuid
        |> field "outcome" _.Outcome executeResult
        |> build
        |> named "ExecuteReply"
