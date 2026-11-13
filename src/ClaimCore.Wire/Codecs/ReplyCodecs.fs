namespace ClaimCore.Wire

open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.Json


module ReplyCodecs =
    open Codec

    let private data codec = record id |> field "data" id codec

    let readReply name (codec: Codec<'T>) : Codec<Outcome<Presented<'T>>> =
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
                        (PresentationCodecs.presented codec)
                    variant
                        "REJECTED"
                        Outcome.Rejected
                        (function
                        | Outcome.Rejected value -> Some value
                        | _ -> None)
                        (record id |> field "rejection" id DiagnosticCodecs.diagnostic)
                    |> namedTaggedVariant "DataRejected"
                    variant
                        "FAILED"
                        Outcome.Failed
                        (function
                        | Outcome.Failed value -> Some value
                        | _ -> None)
                        (record id |> field "fault" id DiagnosticCodecs.fault)
                    |> namedTaggedVariant "Failed"
                ]

        record id |> field "outcome" id outcome |> build |> named name

    let dataReadReply name (codec: Codec<'value>) : Codec<Outcome<'value>> =
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
                        (record id |> field "rejection" id DiagnosticCodecs.diagnostic)
                    |> namedTaggedVariant "DataRejected"
                    variant
                        "FAILED"
                        Outcome.Failed
                        (function
                        | Outcome.Failed value -> Some value
                        | _ -> None)
                        (record id |> field "fault" id DiagnosticCodecs.fault)
                    |> namedTaggedVariant "Failed"
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

    let peopleState =
        (record (fun profile enabled grants access ->
            {
                Profile = profile
                Enabled = enabled
                Grants = grants
                Access = access
            })
        : Fields<PeopleState, _>)
        |> field "profile" _.Profile (nullable ActorCodecs.profile)
        |> field "enabled" _.Enabled boolean
        |> field "grants" _.Grants (list PeopleAdmission.MaximumGrants ResponseCodecs.peopleGrant)
        |> field "access" _.Access (list Capability.all.Length capabilityAccess)
        |> build
        |> named "PeopleState"

    let private peopleReviewEffect =
        tagged
            "PeopleReviewEffect"
            [
                variant
                    (PeopleActKind.token PeopleActKind.Register)
                    PeopleReviewEffect.Register
                    (function
                    | PeopleReviewEffect.Register value -> Some value
                    | _ -> None)
                    (record id |> field "principal" id PrincipalCodecs.key)
                variant
                    (PeopleActKind.token PeopleActKind.ProvisionOwner)
                    PeopleReviewEffect.ProvisionOwner
                    (function
                    | PeopleReviewEffect.ProvisionOwner value -> Some value
                    | _ -> None)
                    (record id |> field "principal" id PrincipalCodecs.key)
                variant
                    (PeopleActKind.token PeopleActKind.SetProfile)
                    PeopleReviewEffect.SetProfile
                    (function
                    | PeopleReviewEffect.SetProfile value -> Some value
                    | _ -> None)
                    (record id |> field "profile" id ActorCodecs.profile)
                variant
                    (PeopleActKind.token PeopleActKind.Grant)
                    PeopleReviewEffect.Grant
                    (function
                    | PeopleReviewEffect.Grant value -> Some value
                    | _ -> None)
                    (record id |> field "grant" id ResponseCodecs.peopleGrant)
                variant
                    (PeopleActKind.token PeopleActKind.Revoke)
                    PeopleReviewEffect.Revoke
                    (function
                    | PeopleReviewEffect.Revoke value -> Some value
                    | _ -> None)
                    (record id |> field "grant" id ResponseCodecs.peopleGrant)
                unitVariant
                    (PeopleActKind.token PeopleActKind.EraseProfile)
                    PeopleReviewEffect.EraseProfile
                unitVariant (PeopleActKind.token PeopleActKind.Enable) PeopleReviewEffect.Enable
                unitVariant (PeopleActKind.token PeopleActKind.Disable) PeopleReviewEffect.Disable
            ]

    let private peopleReviewFields =
        (record (fun basis target scope before after effects ->
            {
                Basis = basis
                Effects = effects
                Target = target
                AccessScope = scope
                Before = before
                After = after
            })
        : Fields<PeopleReview, _>)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "target" _.Target (nullable ResponseCodecs.actorSummary)
        |> field "accessScope" _.AccessScope ResponseCodecs.grantScope
        |> field "before" _.Before peopleState
        |> field "after" _.After peopleState
        |> field "effects" _.Effects (list (PeopleAdmission.MaximumGrants + 2) peopleReviewEffect)

    let private workReviewFields =
        (record (fun parent reference before proposed context ->
            {
                WorkReview.Parent = parent
                Reference = reference
                Before = before
                Proposed = proposed
                Context = context
            })
        : Fields<WorkReview, _>)
        |> field "parent" _.Parent ResponseCodecs.caseSnapshot
        |> field "caseReference" _.Reference ResponseScalars.caseReference
        |> field "before" _.Before WorkCodecs.workReadModel
        |> field "proposed" _.Proposed WorkCodecs.workReadModel
        |> field "context" _.Context ResponseCodecs.runtimeContext

    let private reminderReviewFields =
        (record (fun id parent reference before effects context ->
            {
                ReminderReview.CaseId = id
                Parent = parent
                Reference = reference
                Before = before
                Effects = effects
                Context = context
            })
        : Fields<ReminderReview, _>)
        |> field "caseId" _.CaseId ResponseScalars.caseId
        |> field "parent" _.Parent ResponseCodecs.caseSnapshot
        |> field "caseReference" _.Reference ResponseScalars.caseReference
        |> field "before" _.Before (nullable WorkCodecs.reminder)
        |> field "effects" _.Effects WorkCodecs.effects
        |> field "context" _.Context ResponseCodecs.runtimeContext

    let private noteReviewFields =
        (record (fun parent reference before after act context ->
            {
                NoteReview.Parent = parent
                Reference = reference
                Before = before
                Proposed = after
                Act = act
                Context = context
            })
        : Fields<NoteReview, _>)
        |> field "parent" _.Parent ResponseCodecs.caseSnapshot
        |> field "caseReference" _.Reference ResponseScalars.caseReference
        |> field "before" _.Before (nullable WorkCodecs.note)
        |> field "proposed" _.Proposed WorkCodecs.note
        |> field "act" _.Act (VocabularyCodec.create NoteActKind.vocabulary)
        |> field "context" _.Context ResponseCodecs.runtimeContext

    let review =
        tagged
            "OperationReview"
            [
                variant
                    "NOTE"
                    OperationReview.Note
                    (function
                    | OperationReview.Note value -> Some value
                    | _ -> None)
                    noteReviewFields
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
                    "PEOPLE"
                    OperationReview.People
                    (function
                    | OperationReview.People value -> Some value
                    | _ -> None)
                    peopleReviewFields
            ]

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
                    (record id |> field "rejection" id DiagnosticCodecs.diagnostic)
                |> namedTaggedVariant "DataRejected"
                variant
                    "FAILED"
                    Outcome.Failed
                    (function
                    | Outcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "fault" id DiagnosticCodecs.fault)
                |> namedTaggedVariant "Failed"
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
        |> named "DataPreviewReply"

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
                    (record id |> field "rejection" id DiagnosticCodecs.diagnostic)
                |> namedTaggedVariant "DataRejected"
                variant
                    "FAILED"
                    ExecuteResult.Failed
                    (function
                    | ExecuteResult.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "fault" id DiagnosticCodecs.fault)
                |> namedTaggedVariant "Failed"
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
        |> named "DataExecuteReply"
