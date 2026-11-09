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

    let private noteHandle =
        (record (fun id remove ->
            {
                NoteHandle.NoteId = id
                CanRemove = remove
            })
        : Fields<NoteHandle, _>)
        |> field "noteId" _.NoteId ResponseScalars.noteId
        |> field "canRemove" _.CanRemove boolean
        |> build

    let private caseInteraction =
        (record (fun id revision workRevision history tasks editable correction seeds notes ->
            {
                CaseInteraction.CaseId = id
                Revision = revision
                WorkRevision = workRevision
                History = history
                Tasks = tasks
                EditableInputs = editable
                Correction = correction
                Seeds = seeds
                Notes = notes
            })
        : Fields<CaseInteraction, _>)
        |> field "caseId" _.CaseId ResponseScalars.caseId
        |> field "revision" _.Revision ResponseScalars.positiveRevision
        |> field "workRevision" _.WorkRevision ResponseScalars.revision
        |> field "history" _.History (VocabularyCodec.create HistoryAccess.vocabulary)
        |> field "tasks" _.Tasks (list TaskId.all.Length task)
        |> field "editableInputs" _.EditableInputs (list TaskInputId.all.Length input)
        |> field "correction" _.Correction (nullable ResponseCodecs.correctionAdvice)
        |> field "seeds" _.Seeds seeds
        |> field "notes" _.Notes (list Paging.MaximumPageSize noteHandle)

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

    let private paymentPage =
        let handle =
            (record (fun id remove ->
                {
                    PaymentHandle.PaymentId = id
                    CanRemove = remove
                })
            : Fields<PaymentHandle, _>)
            |> field "paymentId" _.PaymentId ResponseScalars.operationId
            |> field "canRemove" _.CanRemove boolean
            |> build

        (record (fun id revision cursor payments ->
            {
                PaymentPageInteraction.CaseId = id
                Revision = revision
                NextCursor = cursor
                Payments = payments
            })
        : Fields<PaymentPageInteraction, _>)
        |> field "caseId" _.CaseId ResponseScalars.caseId
        |> field "revision" _.Revision ResponseScalars.positiveRevision
        |> field "nextCursor" _.NextCursor nextCursor
        |> field "payments" _.Payments (list PaymentPaging.MaximumLimit handle)

    let private assignees =
        (record (fun cursor actors ->
            {
                ActorPageInteraction.NextCursor = cursor
                ActorIds = actors
            })
        : Fields<ActorPageInteraction, _>)
        |> field "nextCursor" _.NextCursor nextCursor
        |> field "actorIds" _.ActorIds (list Paging.MaximumPageSize ResponseScalars.actorId)

    let private cases =
        (record (fun cursor tasks ids ->
            {
                CasePageInteraction.NextCursor = cursor
                Tasks = tasks
                CaseIds = ids
            })
        : Fields<CasePageInteraction, _>)
        |> field "nextCursor" _.NextCursor nextCursor
        |> field "tasks" _.Tasks (list TaskId.all.Length task)
        |> field "caseIds" _.CaseIds (list Paging.MaximumPageSize ResponseScalars.caseId)

    let private reminders =
        let item = reminder |> build

        (record (fun cursor items ->
            {
                ReminderPageInteraction.NextCursor = cursor
                Reminders = items
            })
        : Fields<ReminderPageInteraction, _>)
        |> field "nextCursor" _.NextCursor nextCursor
        |> field "reminders" _.Reminders (list Paging.MaximumPageSize item)

    let private accessRequests =
        let item =
            (record (fun key principal profile ->
                {
                    AccessRequestHandle.Key = key
                    Principal = principal
                    ProfileSeed = profile
                })
            : Fields<AccessRequestHandle, _>)
            |> field "key" _.Key (boundedText 1 128)
            |> field "principal" _.Principal PrincipalCodecs.key
            |> field "profileSeed" _.ProfileSeed CanonicalInputs.actorProfileInput
            |> build

        (record (fun cursor items ->
            {
                AccessRequestInteraction.NextCursor = cursor
                Requests = items
            })
        : Fields<AccessRequestInteraction, _>)
        |> field "nextCursor" _.NextCursor nextCursor
        |> field "requests" _.Requests (list Paging.MaximumPageSize item)

    let private notePage =
        (record (fun id cursor tasks notes ->
            {
                NotePageInteraction.CaseId = id
                NextCursor = cursor
                Tasks = tasks
                Notes = notes
            })
        : Fields<NotePageInteraction, _>)
        |> field "caseId" _.CaseId ResponseScalars.caseId
        |> field "nextCursor" _.NextCursor nextCursor
        |> field "tasks" _.Tasks (list 2 task)
        |> field "notes" _.Notes (list Paging.MaximumPageSize noteHandle)

    let private person =
        (record (fun actor principal cursor tasks seed ->
            {
                PersonInteraction.ActorId = actor
                Principal = principal
                NextCursor = cursor
                Tasks = tasks
                ProfileSeed = seed
            })
        : Fields<PersonInteraction, _>)
        |> field "actorId" _.ActorId ResponseScalars.actorId
        |> field "principal" _.Principal PrincipalCodecs.key
        |> field "nextCursor" _.NextCursor nextCursor
        |> field "tasks" _.Tasks (list TaskId.all.Length task)
        |> field "profileSeed" _.ProfileSeed (nullable CanonicalInputs.actorProfileInput)

    let context =
        tagged
            "InteractionContext"
            [
                unitVariant "EMPTY" InteractionContext.Empty
                variant
                    "NOTE_PAGE"
                    InteractionContext.NotePage
                    (function
                    | InteractionContext.NotePage value -> Some value
                    | _ -> None)
                    notePage
                variant
                    "PERSON"
                    InteractionContext.Person
                    (function
                    | InteractionContext.Person value -> Some value
                    | _ -> None)
                    person
                variant
                    "ACCESS_REQUESTS"
                    InteractionContext.AccessRequests
                    (function
                    | InteractionContext.AccessRequests value -> Some value
                    | _ -> None)
                    accessRequests
                variant
                    "CASES"
                    InteractionContext.Cases
                    (function
                    | InteractionContext.Cases value -> Some value
                    | _ -> None)
                    cases
                variant
                    "REMINDERS"
                    InteractionContext.Reminders
                    (function
                    | InteractionContext.Reminders value -> Some value
                    | _ -> None)
                    reminders
                variant
                    "PEOPLE"
                    InteractionContext.People
                    (function
                    | InteractionContext.People value -> Some value
                    | _ -> None)
                    assignees
                variant
                    "ASSIGNEES"
                    InteractionContext.Assignees
                    (function
                    | InteractionContext.Assignees value -> Some value
                    | _ -> None)
                    assignees
                variant
                    "PAYMENT_PAGE"
                    InteractionContext.PaymentPage
                    (function
                    | InteractionContext.PaymentPage value -> Some value
                    | _ -> None)
                    paymentPage
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

    let private selectedContext query =
        let pick tag construct extract fields =
            fields |> build |> withLiteral "kind" tag |> map construct extract

        let wrong () =
            invalidArg "context" "Human controls do not match the requested endpoint."

        match query with
        | QueryTaskId.CaseList ->
            pick
                "CASES"
                InteractionContext.Cases
                (function
                | InteractionContext.Cases value -> value
                | _ -> wrong ())
                cases
        | QueryTaskId.ReminderList ->
            pick
                "REMINDERS"
                InteractionContext.Reminders
                (function
                | InteractionContext.Reminders value -> value
                | _ -> wrong ())
                reminders
        | QueryTaskId.AuthorityList ->
            pick
                "PEOPLE"
                InteractionContext.People
                (function
                | InteractionContext.People value -> value
                | _ -> wrong ())
                assignees
        | QueryTaskId.CaseNotes ->
            pick
                "NOTE_PAGE"
                InteractionContext.NotePage
                (function
                | InteractionContext.NotePage value -> value
                | _ -> wrong ())
                notePage
        | QueryTaskId.AuthorityGrants ->
            pick
                "PERSON"
                InteractionContext.Person
                (function
                | InteractionContext.Person value -> value
                | _ -> wrong ())
                person
        | QueryTaskId.AuthorityRequests ->
            pick
                "ACCESS_REQUESTS"
                InteractionContext.AccessRequests
                (function
                | InteractionContext.AccessRequests value -> value
                | _ -> wrong ())
                accessRequests
        | QueryTaskId.CaseGet ->
            pick
                "CASE"
                InteractionContext.Case
                (function
                | InteractionContext.Case value -> value
                | _ -> wrong ())
                caseInteraction
        | QueryTaskId.CaseAssignees ->
            pick
                "ASSIGNEES"
                InteractionContext.Assignees
                (function
                | InteractionContext.Assignees value -> value
                | _ -> wrong ())
                assignees
        | QueryTaskId.CasePayments ->
            pick
                "PAYMENT_PAGE"
                InteractionContext.PaymentPage
                (function
                | InteractionContext.PaymentPage value -> value
                | _ -> wrong ())
                paymentPage
        | QueryTaskId.ReminderGet ->
            pick
                "REMINDER"
                InteractionContext.Reminder
                (function
                | InteractionContext.Reminder value -> value
                | _ -> wrong ())
                reminder
        | QueryTaskId.Context ->
            pick
                "REQUESTER"
                InteractionContext.Requester
                (function
                | InteractionContext.Requester value -> value
                | _ -> wrong ())
                requester
        | QueryTaskId.CaseSummary
        | QueryTaskId.WorkViews
        | QueryTaskId.AuthorityRequestsDismiss ->
            (record (): Fields<unit, unit>)
            |> fixedField "kind" "EMPTY"
            |> build
            |> map (fun () -> InteractionContext.Empty) (function
                | InteractionContext.Empty -> ()
                | _ -> wrong ())
        | QueryTaskId.CaseTimeline
        | QueryTaskId.AuthorityHistory ->
            pick
                "PAGE"
                InteractionContext.Page
                (function
                | InteractionContext.Page value -> value
                | _ -> wrong ())
                page
        | QueryTaskId.OperationGet ->
            invalidArg (nameof query) "Operation lookup has its own outcome contract."

    let readReplyFor query =
        let name = "HumanReadReply_" + QueryTaskId.key query

        let document =
            documentFor
                (selectedContext query)
                id
                id
                basis
                disclosure
                ("HumanReadDocument_" + QueryTaskId.key query)

        let outcome =
            tagged
                (name + ".Outcome")
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
        |> named name
