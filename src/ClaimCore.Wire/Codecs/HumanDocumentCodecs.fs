namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Json

module HumanDocumentCodecs =
    open Codec

    let private input =
        Vocabulary.define "taskInput" TaskInputId.all TaskInputId.token
        |> VocabularyCodec.create
        |> named "TaskInputId"

    let private disclosure =
        Vocabulary.ofUnion<HumanDisclosure> "humanDisclosure" TokenCasing.ScreamingSnake None
        |> VocabularyCodec.create

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
                    | _ -> None)
                    (record id |> field "observation" id ResponseScalars.workDateBasis)
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
                variant
                    "PRINCIPAL"
                    ConfirmationTarget.Principal
                    (function
                    | ConfirmationTarget.Principal value -> Some value
                    | _ -> None)
                    (record id
                     |> field
                         "confirmationText"
                         id
                         (boundedText 1 ConfirmationTarget.maximumPrincipalTextCharacters))
            ]

    let private subjectVariants =
        [
            variant
                "CASE"
                DocumentSubject.Case
                (function
                | DocumentSubject.Case(a, b, c) -> Some(a, b, c)
                | _ -> None)
                (record (fun a b c -> a, b, c)
                 |> field "caseId" (fun (a, _, _) -> a) ResponseScalars.caseId
                 |> field "revision" (fun (_, b, _) -> b) ResponseScalars.revision
                 |> field "workRevision" (fun (_, _, c) -> c) ResponseScalars.revision)
            variant
                "PERSON"
                DocumentSubject.Person
                (function
                | DocumentSubject.Person id -> Some id
                | _ -> None)
                (record id |> field "actorId" id ResponseScalars.actorId)
            variant
                "REQUESTER"
                DocumentSubject.Requester
                (function
                | DocumentSubject.Requester value -> Some value
                | _ -> None)
                (record (fun state installation actor ->
                    {
                        RequesterSubject.State = state
                        InstallationId = installation
                        ActorId = actor
                    })
                 |> field
                     "state"
                     _.State
                     (Vocabulary.ofUnion<RequesterKind>
                         "requesterKind"
                         TokenCasing.ScreamingSnake
                         None
                      |> VocabularyCodec.create)
                 |> field "installationId" _.InstallationId (nullable uuid)
                 |> field "actorId" _.ActorId (nullable ResponseScalars.actorId))
            unitVariant "LIST" DocumentSubject.List
            variant
                "REVIEW"
                DocumentSubject.Review
                (function
                | DocumentSubject.Review(a, b) -> Some(a, b)
                | _ -> None)
                (record (fun a b -> a, b)
                 |> field "confirmation" fst confirmation
                 |> field "consequence" snd (VocabularyCodec.create Consequence.vocabulary))
            variant
                "RECEIPT"
                DocumentSubject.Receipt
                (function
                | DocumentSubject.Receipt value -> Some value
                | _ -> None)
                (record id |> field "operationId" id ResponseScalars.operationId)
            variant
                "OPERATION"
                DocumentSubject.Operation
                (function
                | DocumentSubject.Operation value -> Some value
                | _ -> None)
                (record id |> field "operationId" id uuid)
        ]

    let private subject = tagged "DocumentSubject" subjectVariants

    let private attachmentObservation =
        (record (fun caseId sequence -> { CaseId = caseId; Sequence = sequence })
        : Fields<AttachmentObservation, _>)
        |> field "caseId" _.CaseId ResponseScalars.caseId
        |> field "sequence" _.Sequence AttachmentCodecs.sequence
        |> build
        |> named "AttachmentObservation"

    let private admitDocument value =
        { HumanDocument.create
              value.Subject
              value.Screen
              value.Actions
              value.Links
              value.NextCursor
              value.Basis
              value.Disclosure
              value.History with
            AttachmentObservation = value.AttachmentObservation
        }

    let document =
        record (fun subject screen actions links cursor basis disclosure history observation ->
            {
                HumanDocument.Subject = subject
                Screen = screen
                Actions = actions
                Links = links
                NextCursor = cursor
                Basis = basis
                Disclosure = disclosure
                History = history
                AttachmentObservation = observation
            })
        |> field "subject" _.Subject subject
        |> field "screen" _.Screen ScreenCodecs.screen
        |> field "actions" _.Actions AffordanceCodecs.actions
        |> field "links" _.Links (list LinkCodecs.MaximumLinks LinkCodecs.namedLink)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> field "basis" _.Basis basis
        |> field "disclosure" _.Disclosure disclosure
        |> field
            "history"
            _.History
            (nullable (
                Vocabulary.ofUnion<HistoryAccess> "historyAccess" TokenCasing.ScreamingSnake None
                |> VocabularyCodec.create
            ))
        |> optionalField "attachmentObservation" _.AttachmentObservation attachmentObservation
        |> build
        |> interpret
            (fun path value ->
                try
                    Ok(admitDocument value)
                with :? System.ArgumentException ->
                    Error
                        {
                            Path = path
                            Reason = "Invalid document construction."
                        })
            admitDocument
        |> named "HumanDocument"

    let receiptDocument = document

    let reviewDocument =
        record (fun document presentation ->
            {
                HumanReview.Document = document
                Presentation = presentation
            })
        |> field "document" _.Document document
        |> field "presentation" _.Presentation PresentedValueCodecs.review
        |> build
        |> named "HumanReview"

    let private row =
        integer 0L (int64 (max PeopleAdmission.MaximumGrants PartyContent.MaximumCurrent - 1))
        |> map int int64
        |> named "TaskRowOrdinal"

    let private issue =
        (record (fun input row message ->
            {
                HumanInputIssue.Input = input
                Row =
                    row
                    |> Option.bind (fun ordinal ->
                        let collection =
                            match input with
                            | TaskInputId.Target target -> TaskCollectionId.ofRowInput target
                            | _ -> TaskCollectionId.PeopleGrants

                        TaskRowTarget.create collection ordinal)
                Message = message
            })
        : Fields<HumanInputIssue, _>)
        |> field "input" _.Input input
        |> field "row" (fun value -> value.Row |> Option.map TaskRowTarget.ordinal) (nullable row)
        |> field "message" _.Message PresentedMessageCodecs.message
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
                     |> field "advice" fst PresentedMessageCodecs.advice
                     |> field "issues" snd (boundedList 1 DiagnosticCodecs.MaximumInputIssues issue))
                variant
                    "CONFLICT"
                    HumanRejection.Conflict
                    (function
                    | HumanRejection.Conflict(advice, revision, document) ->
                        Some(advice, revision, document)
                    | _ -> None)
                    (record (fun advice revision document -> advice, revision, document)
                     |> field "advice" (fun (value, _, _) -> value) PresentedMessageCodecs.advice
                     |> field "actualRevision" (fun (_, value, _) -> value) ResponseScalars.revision
                     |> field "document" (fun (_, _, value) -> value) document)
                variant
                    "REFUSED"
                    HumanRejection.Refused
                    (function
                    | HumanRejection.Refused value -> Some value
                    | _ -> None)
                    (record id |> field "advice" id PresentedMessageCodecs.advice)
            ]

    let readReply =
        let outcome =
            tagged
                "HumanReadReply.Outcome"
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
                    |> namedTaggedVariant "HumanRejected"
                    variant
                        "FAILED"
                        HumanReadOutcome.Failed
                        (function
                        | HumanReadOutcome.Failed value -> Some value
                        | _ -> None)
                        (record id |> field "fault" id DiagnosticCodecs.fault)
                    |> namedTaggedVariant "Failed"
                ]

        (record (fun outcome -> { HumanReadReply.Outcome = outcome }): Fields<HumanReadReply, _>)
        |> field "outcome" _.Outcome outcome
        |> build
        |> named "HumanReadResult"
