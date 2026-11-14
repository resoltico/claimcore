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

    let private snapshotBasis =
        record ObservationBasis.Snapshot
        |> fixedField "kind" "SNAPSHOT"
        |> field
            "observation"
            (function
            | ObservationBasis.Snapshot value -> value
            | _ -> invalidArg "basis" "A receipt or review has an observed snapshot.")
            ResponseScalars.workDateBasis
        |> build

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

    let private selectedSubject token =
        subjectVariants
        |> List.filter (fun value -> value.Tag = token)
        |> tagged ("SubjectFor" + token)

    let private documentFor subject selectedBasis name =
        record (fun subject screen actions links cursor basis disclosure history ->
            {
                HumanDocument.Subject = subject
                Screen = screen
                Actions = actions
                Links = links
                NextCursor = cursor
                Basis = basis
                Disclosure = disclosure
                History = history
            })
        |> field "subject" _.Subject subject
        |> field "screen" _.Screen PresentationCodecs.view
        |> field "actions" _.Actions (list TaskId.all.Length PresentationCodecs.affordance)
        |> field
            "links"
            _.Links
            (list
                (CaseView.all.Length + ReminderView.all.Length + QueryTaskId.all.Length)
                PresentationCodecs.namedLink)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> field "basis" _.Basis selectedBasis
        |> field "disclosure" _.Disclosure disclosure
        |> field
            "history"
            _.History
            (nullable (
                Vocabulary.ofUnion<HistoryAccess> "historyAccess" TokenCasing.ScreamingSnake None
                |> VocabularyCodec.create
            ))
        |> build
        |> interpret
            (fun path value ->
                try
                    Ok(
                        HumanDocument.create
                            value.Subject
                            value.Screen
                            value.Actions
                            value.Links
                            value.NextCursor
                            value.Basis
                            value.Disclosure
                            value.History
                    )
                with :? System.ArgumentException ->
                    Error
                        {
                            Path = path
                            Reason = "Invalid document construction."
                        })
            (fun value ->
                HumanDocument.create
                    value.Subject
                    value.Screen
                    value.Actions
                    value.Links
                    value.NextCursor
                    value.Basis
                    value.Disclosure
                    value.History)
        |> named name

    let document = documentFor subject basis "HumanDocument"

    let receiptDocument =
        documentFor (selectedSubject "RECEIPT") snapshotBasis "HumanReceiptDocument"

    let reviewDocument =
        record (fun document presentation ->
            {
                HumanReview.Document = document
                Presentation = presentation
            })
        |> field
            "document"
            _.Document
            (documentFor (selectedSubject "REVIEW") snapshotBasis "HumanReviewDocument")
        |> field "presentation" _.Presentation PresentationCodecs.review
        |> build
        |> named "HumanReview"

    let private row =
        integer 0L (int64 PeopleAdmission.MaximumGrants - 1L)
        |> map
            (int >> TaskRowTarget.create TaskCollectionId.PeopleGrants >> Option.get)
            (TaskRowTarget.ordinal >> int64)
        |> named "TaskRowOrdinal"

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
                     |> field "advice" fst PresentationCodecs.advice
                     |> field "issues" snd (boundedList 1 DiagnosticCodecs.MaximumInputIssues issue))
                variant
                    "CONFLICT"
                    HumanRejection.Conflict
                    (function
                    | HumanRejection.Conflict(advice, revision, document) ->
                        Some(advice, revision, document)
                    | _ -> None)
                    (record (fun advice revision document -> advice, revision, document)
                     |> field "advice" (fun (value, _, _) -> value) PresentationCodecs.advice
                     |> field "actualRevision" (fun (_, value, _) -> value) ResponseScalars.revision
                     |> field "document" (fun (_, _, value) -> value) document)
                variant
                    "REFUSED"
                    HumanRejection.Refused
                    (function
                    | HumanRejection.Refused value -> Some value
                    | _ -> None)
                    (record id |> field "advice" id PresentationCodecs.advice)
            ]

    let readReplyFor query =
        let name = "HumanReadReply_" + TaskId.key query

        let token =
            match query with
            | TaskId.Query(QueryTaskId.CaseGet | QueryTaskId.CasePayments) -> "CASE"
            | TaskId.Query QueryTaskId.Context -> "REQUESTER"
            | TaskId.Query QueryTaskId.PeopleGrants -> "PERSON"
            | _ -> "LIST"

        let document =
            documentFor (selectedSubject token) basis ("HumanReadDocument_" + TaskId.key query)

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
        |> named name
