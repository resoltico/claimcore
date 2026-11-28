namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// Binding shape follows the declaration; codecs contain no task identity dispatch.
module BindingCodecs =
    open Codec
    open RecordScalars

    let caseSelector =
        tagged
            "CaseSelector"
            [
                variant
                    "REFERENCE"
                    CaseSelector.Reference
                    (function
                    | CaseSelector.Reference value -> Some value
                    | _ -> None)
                    (record id |> field "caseReference" id text)
                variant
                    "ID"
                    CaseSelector.Id
                    (function
                    | CaseSelector.Id value -> Some value
                    | _ -> None)
                    (record id |> field "caseId" id caseId)
            ]
        |> vocabulary "caseSelector"

    let private mismatch () =
        invalidArg "binding" "The binding does not match its declared shape."

    let private version minimum = revision minimum
    let private zero = integer 0L 0L |> map (fun _ -> Revision.zero) Revision.value

    let private positiveSequence =
        integer 1L SafeInteger.Maximum
        |> map (Sequence.create >> Option.get) Sequence.value

    let caseNew =
        (record Binding.CaseNew: Fields<Binding, _>)
        |> fixedField "kind" "CASE"
        |> field
            "expectedRevision"
            (function
            | Binding.CaseNew value -> value
            | _ -> mismatch ())
            zero
        |> build
        |> named "Binding.CASE.new"

    let caseExisting =
        (record (fun target revision -> Binding.Case(target, revision)): Fields<Binding, _>)
        |> fixedField "kind" "CASE"
        |> field
            "target"
            (function
            | Binding.Case(target, _) -> target
            | _ -> mismatch ())
            caseSelector
        |> field
            "expectedRevision"
            (function
            | Binding.Case(_, revision) -> revision
            | _ -> mismatch ())
            (version 1L)
        |> build
        |> named "Binding.CASE"

    let casePayment =
        (record (fun target revision payment -> Binding.CasePayment(target, revision, payment))
        : Fields<Binding, _>)
        |> fixedField "kind" "CASE"
        |> field
            "target"
            (function
            | Binding.CasePayment(target, _, _) -> target
            | _ -> mismatch ())
            caseSelector
        |> field
            "expectedRevision"
            (function
            | Binding.CasePayment(_, revision, _) -> revision
            | _ -> mismatch ())
            (version 1L)
        |> field
            "paymentId"
            (function
            | Binding.CasePayment(_, _, payment) -> payment
            | _ -> mismatch ())
            operationId
        |> build
        |> named "Binding.CASE.payment"

    let erasureReviewToken = boundedText 1 ErasureReview.MaximumTokenCharacters

    let private commitment =
        patternedText
            ErasureReview.CommitmentCharacters
            ErasureReview.CommitmentCharacters
            "^[0-9a-f]{64}$"
            None

    let caseErase =
        (record (fun target revision sequence digest ->
            Binding.CaseErase(target, revision, ErasureReview.Accepted(sequence, digest)))
        : Fields<Binding, _>)
        |> fixedField "kind" "CASE"
        |> field
            "target"
            (function
            | Binding.CaseErase(target, _, _) -> target
            | _ -> mismatch ())
            caseSelector
        |> field
            "expectedRevision"
            (function
            | Binding.CaseErase(_, revision, _) -> revision
            | _ -> mismatch ())
            (version 1L)
        |> field
            "expectedFamilySequence"
            (function
            | Binding.CaseErase(_, _, ErasureReview.Accepted(sequence, _)) -> sequence
            | _ -> mismatch ())
            positiveSequence
        |> field
            "reviewCommitment"
            (function
            | Binding.CaseErase(_, _, ErasureReview.Accepted(_, digest)) -> digest
            | _ -> mismatch ())
            commitment
        |> build
        |> named "RetainedBinding.CASE.erase"

    let private eraseReview =
        (record (fun target revision token ->
            Binding.CaseErase(target, revision, ErasureReview.Token token))
        : Fields<Binding, _>)
        |> fixedField "kind" "CASE"
        |> field
            "target"
            (function
            | Binding.CaseErase(target, _, _) -> target
            | _ -> mismatch ())
            caseSelector
        |> field
            "expectedRevision"
            (function
            | Binding.CaseErase(_, revision, _) -> revision
            | _ -> mismatch ())
            (version 1L)
        |> field
            "erasureReviewToken"
            (function
            | Binding.CaseErase(_, _, ErasureReview.Token token) -> token
            | _ -> mismatch ())
            erasureReviewToken
        |> build
        |> named "Binding.CASE.erase"

    let work =
        (record (fun target revision -> Binding.Work(target, revision)): Fields<Binding, _>)
        |> fixedField "kind" "WORK"
        |> field
            "target"
            (function
            | Binding.Work(target, _) -> target
            | _ -> mismatch ())
            caseSelector
        |> field
            "expectedRevision"
            (function
            | Binding.Work(_, revision) -> revision
            | _ -> mismatch ())
            (version 0L)
        |> build
        |> named "Binding.WORK"

    let reminderNew =
        (record Binding.ReminderNew: Fields<Binding, _>)
        |> fixedField "kind" "REMINDER"
        |> field
            "target"
            (function
            | Binding.ReminderNew target -> target
            | _ -> mismatch ())
            caseSelector
        |> build
        |> named "Binding.REMINDER.new"

    let reminder =
        (record (fun target id revision -> Binding.Reminder(target, id, revision))
        : Fields<Binding, _>)
        |> fixedField "kind" "REMINDER"
        |> field
            "target"
            (function
            | Binding.Reminder(target, _, _) -> target
            | _ -> mismatch ())
            caseSelector
        |> field
            "reminderId"
            (function
            | Binding.Reminder(_, id, _) -> id
            | _ -> mismatch ())
            reminderId
        |> field
            "expectedRevision"
            (function
            | Binding.Reminder(_, _, revision) -> revision
            | _ -> mismatch ())
            (version 1L)
        |> build
        |> named "Binding.REMINDER"

    let noteNew =
        (record Binding.NoteNew: Fields<Binding, _>)
        |> fixedField "kind" "NOTE"
        |> field
            "target"
            (function
            | Binding.NoteNew target -> target
            | _ -> mismatch ())
            caseSelector
        |> build
        |> named "Binding.NOTE.new"

    let note =
        let identity = identifier NoteId.create NoteId.value

        (record (fun target id -> Binding.Note(target, id)): Fields<Binding, _>)
        |> fixedField "kind" "NOTE"
        |> field
            "target"
            (function
            | Binding.Note(target, _) -> target
            | _ -> mismatch ())
            caseSelector
        |> field
            "noteId"
            (function
            | Binding.Note(_, id) -> id
            | _ -> mismatch ())
            identity
        |> build
        |> named "Binding.NOTE"

    let attachmentNew =
        (record Binding.AttachmentNew: Fields<Binding, _>)
        |> fixedField "kind" "ATTACHMENT"
        |> field
            "target"
            (function
            | Binding.AttachmentNew target -> target
            | _ -> mismatch ())
            caseSelector
        |> build
        |> named "Binding.ATTACHMENT.new"

    let attachment =
        let identity = identifier AttachmentId.create AttachmentId.value

        (record (fun target id -> Binding.Attachment(target, id)): Fields<Binding, _>)
        |> fixedField "kind" "ATTACHMENT"
        |> field
            "target"
            (function
            | Binding.Attachment(target, _) -> target
            | _ -> mismatch ())
            caseSelector
        |> field
            "attachmentId"
            (function
            | Binding.Attachment(_, id) -> id
            | _ -> mismatch ())
            identity
        |> build
        |> named "Binding.ATTACHMENT"

    let linkNew =
        (record Binding.LinkNew: Fields<Binding, _>)
        |> fixedField "kind" "LINK"
        |> field
            "target"
            (function
            | Binding.LinkNew target -> target
            | _ -> mismatch ())
            caseSelector
        |> build
        |> named "Binding.LINK.new"

    let link =
        let identity = identifier LinkId.create LinkId.value

        (record (fun target id -> Binding.Link(target, id)): Fields<Binding, _>)
        |> fixedField "kind" "LINK"
        |> field
            "target"
            (function
            | Binding.Link(target, _) -> target
            | _ -> mismatch ())
            caseSelector
        |> field
            "linkId"
            (function
            | Binding.Link(_, id) -> id
            | _ -> mismatch ())
            identity
        |> build
        |> named "Binding.LINK"

    let peopleNew =
        (record Binding.PeopleNew: Fields<Binding, _>)
        |> fixedField "kind" "PEOPLE"
        |> field
            "expectedRevision"
            (function
            | Binding.PeopleNew value -> value
            | _ -> mismatch ())
            zero
        |> build
        |> named "Binding.PEOPLE.new"

    let people =
        (record (fun id revision -> Binding.People(id, revision)): Fields<Binding, _>)
        |> fixedField "kind" "PEOPLE"
        |> field
            "actorId"
            (function
            | Binding.People(id, _) -> id
            | _ -> mismatch ())
            actorId
        |> field
            "expectedRevision"
            (function
            | Binding.People(_, revision) -> revision
            | _ -> mismatch ())
            (version 1L)
        |> build
        |> named "Binding.PEOPLE"

    let partyNew =
        (record Binding.PartyNew: Fields<Binding, _>)
        |> fixedField "kind" "PARTY"
        |> field
            "target"
            (function
            | Binding.PartyNew target -> target
            | _ -> mismatch ())
            caseSelector
        |> build
        |> named "Binding.PARTY.new"

    let party =
        (record (fun target id revision -> Binding.Party(target, id, revision)): Fields<Binding, _>)
        |> fixedField "kind" "PARTY"
        |> field
            "target"
            (function
            | Binding.Party(target, _, _) -> target
            | _ -> mismatch ())
            caseSelector
        |> field
            "partyId"
            (function
            | Binding.Party(_, id, _) -> id
            | _ -> mismatch ())
            (identifier PartyId.create PartyId.value)
        |> field
            "expectedRevision"
            (function
            | Binding.Party(_, _, revision) -> revision
            | _ -> mismatch ())
            (version 1L)
        |> build
        |> named "Binding.PARTY"

    let private forShape =
        function
        | BindingShape.CaseNew -> caseNew
        | BindingShape.Case -> caseExisting
        | BindingShape.CasePayment -> casePayment
        | BindingShape.CaseErase -> caseErase
        | BindingShape.Work -> work
        | BindingShape.ReminderNew -> reminderNew
        | BindingShape.Reminder -> reminder
        | BindingShape.NoteNew -> noteNew
        | BindingShape.Note -> note
        | BindingShape.AttachmentNew -> attachmentNew
        | BindingShape.Attachment -> attachment
        | BindingShape.PartyNew -> partyNew
        | BindingShape.Party -> party
        | BindingShape.LinkNew -> linkNew
        | BindingShape.Link -> link
        | BindingShape.PeopleNew -> peopleNew
        | BindingShape.People -> people

    let private select codec shape =
        branch
            id
            (fun value ->
                if BindingShape.ofValue value = shape then
                    Some value
                else
                    None)
            codec

    let forDeclaration declaration =
        match TaskDeclaration.bindingShapes declaration with
        | [ shape ] -> forShape shape
        | shapes ->
            choice
                "Binding.PEOPLE.recover"
                (List.map (fun shape -> select (forShape shape) shape) shapes)

    let forValue = BindingShape.ofValue >> forShape

    let matches declaration value =
        if
            not (
                List.contains
                    (BindingShape.ofValue value)
                    (TaskDeclaration.bindingShapes declaration)
            )
        then
            false
        else
            let actual = forValue value

            try
                actual.Write value |> actual.Validate "$" |> Result.isOk
            with :? System.ArgumentException ->
                false

    let binding =
        choice
            "RetainedBinding"
            (BindingShape.all |> List.map (fun shape -> select (forShape shape) shape))

    let reviewBinding =
        choice
            "Binding"
            (BindingShape.all
             |> List.map (fun shape ->
                 select
                     (if shape = BindingShape.CaseErase then
                          eraseReview
                      else
                          forShape shape)
                     shape))
