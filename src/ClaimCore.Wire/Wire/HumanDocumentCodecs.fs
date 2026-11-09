namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.RecordFormat
open ClaimCore.Json

module HumanDocumentCodecs =
    open Codec

    let private taskVocabulary = Vocabulary.define "task" TaskId.all TaskId.key
    let private task = VocabularyCodec.create taskVocabulary

    let private operationTask =
        VocabularyCodec.select
            (function
            | TaskId.Operation _ -> true
            | TaskId.Query _ -> false)
            taskVocabulary

    let private input =
        Vocabulary.define "taskInput" TaskInputId.all TaskInputId.token
        |> VocabularyCodec.create
        |> named "TaskInputId"

    let private disclosureVocabulary =
        Vocabulary.ofUnion<HumanDisclosure> "humanDisclosure" TokenCasing.ScreamingSnake None

    let private disclosure = VocabularyCodec.create disclosureVocabulary

    let private basis =
        tagged
            "ObservationBasis"
            [
                unitVariant "UNAVAILABLE" ObservationBasis.Unavailable
                variant
                    "SNAPSHOT"
                    ObservationBasis.Snapshot
                    (function
                    | ObservationBasis.Snapshot value -> Some value
                    | ObservationBasis.Unavailable -> None)
                    (record id |> field "observation" id ResponseScalars.workDateBasis)
            ]

    let private workSeed =
        (record (fun summary responsible ->
            {
                WorkInputSeed.Summary = summary
                Responsible = responsible
            })
        : Fields<WorkInputSeed, _>)
        |> field "summary" _.Summary (nullable (boundedUtf8Text 0 HandlingSummary.MaximumBytes))
        |> field "responsible" _.Responsible (nullable ResponseScalars.actorId)
        |> build
        |> named "WorkInputSeed"

    let private seeds =
        (record (fun registration decision work ->
            {
                CaseInputSeeds.Registration = registration
                Decision = decision
                Work = work
            })
        : Fields<CaseInputSeeds, _>)
        |> field "registration" _.Registration (nullable CanonicalInputs.registration)
        |> field "decision" _.Decision (nullable CanonicalInputs.decision)
        |> field "work" _.Work (nullable workSeed)
        |> build
        |> named "CaseInputSeeds"

    let private caseInteraction =
        (record (fun id revision workRevision tasks editable correction seeds ->
            {
                CaseInteraction.CaseId = id
                Revision = revision
                WorkRevision = workRevision
                Tasks = tasks
                EditableInputs = editable
                Correction = correction
                Seeds = seeds
            })
        : Fields<CaseInteraction, _>)
        |> field "caseId" _.CaseId ResponseScalars.caseId
        |> field "revision" _.Revision ResponseScalars.positiveRevision
        |> field "workRevision" _.WorkRevision ResponseScalars.revision
        |> field "tasks" _.Tasks (list TaskId.all.Length task)
        |> field "editableInputs" _.EditableInputs (list TaskInputId.all.Length input)
        |> field "correction" _.Correction (nullable ResponseCodecs.correctionAdvice)
        |> field "seeds" _.Seeds seeds

    let private nextCursor =
        nullable (boundedText 1 Paging.MaximumAccessRequestCursorCharacters)

    let private requester =
        (record (fun state installation actor views tasks scoped ->
            {
                RequesterInteraction.State = state
                InstallationId = installation
                ActorId = actor
                Views = views
                Tasks = tasks
                HasScopedCases = scoped
            })
        : Fields<RequesterInteraction, _>)
        |> field "state" _.State (VocabularyCodec.create RequesterKind.vocabulary)
        |> field "installationId" _.InstallationId (nullable uuid)
        |> field "actorId" _.ActorId (nullable ResponseScalars.actorId)
        |> field "views" _.Views (list 3 (VocabularyCodec.create View.vocabulary))
        |> field "tasks" _.Tasks (list TaskId.all.Length task)
        |> field "hasScopedCases" _.HasScopedCases boolean

    let private page =
        (record (fun cursor tasks ->
            {
                PageInteraction.NextCursor = cursor
                Tasks = tasks
            })
        : Fields<PageInteraction, _>)
        |> field "nextCursor" _.NextCursor nextCursor
        |> field "tasks" _.Tasks (list TaskId.all.Length task)

    let private reminder =
        (record (fun parent id revision tasks action due ->
            {
                ReminderInteraction.CaseId = parent
                ReminderId = id
                Revision = revision
                Tasks = tasks
                ActionSeed = action
                DueDateSeed = due
            })
        : Fields<ReminderInteraction, _>)
        |> field "caseId" _.CaseId ResponseScalars.caseId
        |> field "reminderId" _.ReminderId ResponseScalars.reminderId
        |> field "revision" _.Revision ResponseScalars.positiveRevision
        |> field "tasks" _.Tasks (list ReminderActKind.all.Length task)
        |> field
            "actionSeed"
            _.ActionSeed
            (nullable (boundedUtf8Text 0 ReminderAction.MaximumBytes))
        |> field "dueDateSeed" _.DueDateSeed (nullable ResponseScalars.date)

    let private reminderIdentity =
        (record (fun id revision ->
            {
                ReminderIdentity.ReminderId = id
                Revision = revision
            })
        : Fields<ReminderIdentity, _>)
        |> field "reminderId" _.ReminderId ResponseScalars.reminderId
        |> field "revision" _.Revision ResponseScalars.positiveRevision
        |> build
        |> named "ReminderIdentity"

    let private reviewTarget =
        tagged
            "ReviewTarget"
            [
                variant
                    "CASE"
                    ReviewTarget.Case
                    (function
                    | ReviewTarget.Case(id, revision) -> Some(id, revision)
                    | _ -> None)
                    (record (fun id revision -> id, revision)
                     |> field "caseId" fst ResponseScalars.caseId
                     |> field "beforeRevision" snd (nullable ResponseScalars.positiveRevision))
                variant
                    "WORK"
                    ReviewTarget.Work
                    (function
                    | ReviewTarget.Work(id, revision) -> Some(id, revision)
                    | _ -> None)
                    (record (fun id revision -> id, revision)
                     |> field "caseId" fst ResponseScalars.caseId
                     |> field "beforeRevision" snd ResponseScalars.revision)
                variant
                    "NOTE"
                    ReviewTarget.Note
                    (function
                    | ReviewTarget.Note(id, existing) -> Some(id, existing)
                    | _ -> None)
                    (record (fun id existing -> id, existing)
                     |> field "caseId" fst ResponseScalars.caseId
                     |> field "existingNoteId" snd (nullable ResponseScalars.noteId))
                variant
                    "REMINDER"
                    ReviewTarget.Reminder
                    (function
                    | ReviewTarget.Reminder(id, existing) -> Some(id, existing)
                    | _ -> None)
                    (record (fun id existing -> id, existing)
                     |> field "caseId" fst ResponseScalars.caseId
                     |> field "existing" snd (nullable reminderIdentity))
                variant
                    "AUTHORITY"
                    ReviewTarget.Authority
                    (function
                    | ReviewTarget.Authority(id, scope) -> Some(id, scope)
                    | _ -> None)
                    (record (fun id scope -> id, scope)
                     |> field "actorId" fst (nullable ResponseScalars.actorId)
                     |> field "scope" snd ResponseCodecs.grantScope)
            ]

    let private confirmation =
        tagged
            "ConfirmationTarget"
            [
                variant
                    "CASE_REFERENCE"
                    ConfirmationTarget.CaseReference
                    (function
                    | ConfirmationTarget.CaseReference value -> Some value
                    | _ -> None)
                    (record id |> field "reference" id ResponseScalars.caseReference)
                variant
                    "OPERATION"
                    ConfirmationTarget.Operation
                    (function
                    | ConfirmationTarget.Operation value -> Some value
                    | _ -> None)
                    (record id |> field "operationId" id uuid)
            ]

    let private review =
        (record (fun task target inputs consequence confirmation ->
            {
                ReviewInteraction.Task = task
                Target = target
                Inputs = inputs
                Consequence = consequence
                Confirmation = confirmation
            })
        : Fields<ReviewInteraction, _>)
        |> field "task" _.Task operationTask
        |> field "target" _.Target reviewTarget
        |> field "inputs" _.Inputs (list TaskInputId.all.Length input)
        |> field "consequence" _.Consequence (VocabularyCodec.create Consequence.vocabulary)
        |> field "confirmation" _.Confirmation confirmation

    let private receiptTarget =
        tagged
            "ReceiptTarget"
            [
                variant
                    "CASE"
                    ReceiptTarget.Case
                    (function
                    | ReceiptTarget.Case(id, revision) -> Some(id, revision)
                    | _ -> None)
                    (record (fun id revision -> id, revision)
                     |> field "caseId" fst ResponseScalars.caseId
                     |> field "revision" snd ResponseScalars.positiveRevision)
                variant
                    "WORK"
                    ReceiptTarget.Work
                    (function
                    | ReceiptTarget.Work(id, revision) -> Some(id, revision)
                    | _ -> None)
                    (record (fun id revision -> id, revision)
                     |> field "caseId" fst ResponseScalars.caseId
                     |> field "revision" snd ResponseScalars.positiveRevision)
                variant
                    "NOTE"
                    ReceiptTarget.Note
                    (function
                    | ReceiptTarget.Note(id, note) -> Some(id, note)
                    | _ -> None)
                    (record (fun id note -> id, note)
                     |> field "caseId" fst ResponseScalars.caseId
                     |> field "noteId" snd ResponseScalars.noteId)
                variant
                    "REMINDER"
                    ReceiptTarget.Reminder
                    (function
                    | ReceiptTarget.Reminder(id, versions) -> Some(id, versions)
                    | _ -> None)
                    (record (fun id versions -> id, versions)
                     |> field "caseId" fst ResponseScalars.caseId
                     |> field "versions" snd (boundedList 1 2 reminderIdentity))
                variant
                    "AUTHORITY"
                    ReceiptTarget.Authority
                    (function
                    | ReceiptTarget.Authority value -> Some value
                    | _ -> None)
                    (record id |> field "actorId" id ResponseScalars.actorId)
            ]

    let private receipt =
        (record (fun target refresh ->
            {
                ReceiptInteraction.Target = target
                RefreshCase = refresh
            })
        : Fields<ReceiptInteraction, _>)
        |> field "target" _.Target receiptTarget
        |> field "refreshCase" _.RefreshCase (nullable ResponseScalars.caseId)

    let private snapshotBasis =
        record ObservationBasis.Snapshot
        |> field
            "observation"
            (function
            | ObservationBasis.Snapshot value -> value
            | ObservationBasis.Unavailable ->
                invalidArg "basis" "A review or receipt requires an observation.")
            ResponseScalars.workDateBasis
        |> build
        |> withLiteral "kind" "SNAPSHOT"
        |> named "SnapshotObservationBasis"

    let context =
        tagged
            "InteractionContext"
            [
                unitVariant "EMPTY" InteractionContext.Empty
                variant
                    "REVIEW"
                    InteractionContext.Review
                    (function
                    | InteractionContext.Review value -> Some value
                    | _ -> None)
                    review
                variant
                    "RECEIPT"
                    InteractionContext.Receipt
                    (function
                    | InteractionContext.Receipt value -> Some value
                    | _ -> None)
                    receipt
                variant
                    "REMINDER"
                    InteractionContext.Reminder
                    (function
                    | InteractionContext.Reminder value -> Some value
                    | _ -> None)
                    reminder
                variant
                    "CASE"
                    InteractionContext.Case
                    (function
                    | InteractionContext.Case value -> Some value
                    | _ -> None)
                    caseInteraction
                variant
                    "CONTINUATION"
                    InteractionContext.Continuation
                    (function
                    | InteractionContext.Continuation value -> Some value
                    | _ -> None)
                    (record id |> field "nextCursor" id nextCursor)
                variant
                    "REQUESTER"
                    InteractionContext.Requester
                    (function
                    | InteractionContext.Requester value -> Some value
                    | _ -> None)
                    requester
                variant
                    "PAGE"
                    InteractionContext.Page
                    (function
                    | InteractionContext.Page value -> Some value
                    | _ -> None)
                    page
            ]

    let private documentFor contextCodec construct extract selectedBasis selectedDisclosure name =
        (record (fun screen context basis disclosure ->
            {
                HumanDocument.Screen = screen
                Context = construct context
                Basis = basis
                Disclosure = disclosure
            })
        : Fields<HumanDocument, _>)
        |> field "screen" _.Screen PresentationCodecs.view
        |> field "context" (_.Context >> extract) contextCodec
        |> field "basis" _.Basis selectedBasis
        |> field "disclosure" _.Disclosure selectedDisclosure
        |> build
        |> fun codec -> restrict codec.Schema HumanDocumentStructure.validate codec
        |> named name

    let document = documentFor context id id basis disclosure "HumanDocument"

    let private reviewHumanDocument =
        documentFor
            (review |> build |> withLiteral "kind" "REVIEW")
            InteractionContext.Review
            (function
            | InteractionContext.Review value -> value
            | _ -> invalidArg "context" "A review requires reviewed command bindings.")
            snapshotBasis
            (VocabularyCodec.select ((=) HumanDisclosure.Current) disclosureVocabulary)
            "HumanReviewDocument"

    let receiptDocument =
        documentFor
            (receipt |> build |> withLiteral "kind" "RECEIPT")
            InteractionContext.Receipt
            (function
            | InteractionContext.Receipt value -> value
            | _ -> invalidArg "context" "A receipt requires accepted resource bindings.")
            snapshotBasis
            (VocabularyCodec.select
                (fun value ->
                    List.contains
                        value
                        [
                            HumanDisclosure.Current
                            HumanDisclosure.Partial
                            HumanDisclosure.Withheld
                            HumanDisclosure.Erased
                        ])
                disclosureVocabulary)
            "HumanReceiptDocument"

    let reviewDocument =
        (record (fun document presentation ->
            {
                HumanReview.Document = document
                Presentation = presentation
            })
        : Fields<HumanReview, _>)
        |> field "document" _.Document reviewHumanDocument
        |> field "presentation" _.Presentation PresentationCodecs.review
        |> build
        |> named "HumanReview"

    let advice =
        (record (fun message action ->
            {
                HumanAdvice.Message = message
                Action = action
            })
        : Fields<HumanAdvice, _>)
        |> field "message" _.Message PresentationCodecs.message
        |> field "action" _.Action (VocabularyCodec.create RecommendedAction.vocabulary)
        |> build
        |> named "HumanAdvice"

    let private row =
        let collection =
            Vocabulary.ofUnion<TaskCollectionId> "taskCollection" TokenCasing.CamelCase None
            |> VocabularyCodec.create

        record (fun collection ordinal -> TaskRowTarget.create collection ordinal |> Option.get)
        |> field "collection" TaskRowTarget.collection collection
        |> field
            "submittedOrdinal"
            TaskRowTarget.ordinal
            (integer 0L (int64 AuthorityAdmission.MaximumGrants - 1L) |> map int int64)
        |> build
        |> named "TaskRowTarget"

    let private issue =
        (record (fun input row message related ->
            {
                HumanInputIssue.Input = input
                Row = row
                Message = message
                Related = related
            })
        : Fields<HumanInputIssue, _>)
        |> field "input" _.Input input
        |> field "row" _.Row (nullable row)
        |> field "message" _.Message PresentationCodecs.message
        |> field "related" _.Related (nullable PresentationCodecs.entry)
        |> build
        |> named "HumanInputIssue"

    let rejection =
        tagged
            "HumanRejection"
            [
                variant
                    "INPUT"
                    HumanRejection.Input
                    (function
                    | HumanRejection.Input(advice, issues) -> Some(advice, issues)
                    | _ -> None)
                    (record (fun advice issues -> advice, issues)
                     |> field "advice" fst advice
                     |> field "issues" snd (boundedList 1 DiagnosticCodecs.MaximumInputIssues issue))
                variant
                    "CONFLICT"
                    HumanRejection.Conflict
                    (function
                    | HumanRejection.Conflict(advice, revision, document) ->
                        Some(advice, revision, document)
                    | _ -> None)
                    (record (fun advice revision document -> advice, revision, document)
                     |> field "advice" (fun (value, _, _) -> value) advice
                     |> field "actualRevision" (fun (_, value, _) -> value) ResponseScalars.revision
                     |> field "document" (fun (_, _, value) -> value) document)
                variant
                    "REFUSED"
                    HumanRejection.Refused
                    (function
                    | HumanRejection.Refused value -> Some value
                    | _ -> None)
                    (record id |> field "advice" id advice)
            ]

    let readReply =
        let outcome =
            tagged
                "HumanReadOutcome"
                [
                    variant
                        "SUCCEEDED"
                        HumanReadOutcome.Succeeded
                        (function
                        | HumanReadOutcome.Succeeded value -> Some value
                        | _ -> None)
                        (record id |> field "document" id document)
                    variant
                        "REJECTED"
                        HumanReadOutcome.Rejected
                        (function
                        | HumanReadOutcome.Rejected value -> Some value
                        | _ -> None)
                        (record id |> field "rejection" id rejection)
                    variant
                        "FAILED"
                        HumanReadOutcome.Failed
                        (function
                        | HumanReadOutcome.Failed value -> Some value
                        | _ -> None)
                        (record id |> field "advice" id advice)
                ]

        (record (fun outcome -> { HumanReadReply.Outcome = outcome }): Fields<HumanReadReply, _>)
        |> field "outcome" _.Outcome outcome
        |> build
        |> named "HumanReadReply"
