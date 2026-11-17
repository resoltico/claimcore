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

    let caseErase =
        (record (fun target revision sequence -> Binding.CaseErase(target, revision, sequence))
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
            | Binding.CaseErase(_, _, sequence) -> sequence
            | _ -> mismatch ())
            positiveSequence
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

    let forDeclaration (declaration: TaskDeclaration) =
        match declaration.Target with
        | ActSubject.Case when declaration.Creates -> caseNew
        | ActSubject.Case when declaration.ReviewsFamilySequence -> caseErase
        | ActSubject.Case when
            declaration.Inputs
            |> List.exists (fun input ->
                input.Input = TaskInputId.Target InputTarget.PaymentId
                && input.Source = InputSource.Binding)
            ->
            casePayment
        | ActSubject.Case -> caseExisting
        | ActSubject.Work -> work
        | ActSubject.Note when declaration.Creates -> noteNew
        | ActSubject.Note -> note
        | ActSubject.Reminder when declaration.Creates -> reminderNew
        | ActSubject.Reminder -> reminder
        | ActSubject.People when declaration.Creates -> peopleNew
        | ActSubject.People -> people

    let private select codec accepts =
        branch id (fun value -> if accepts value then Some value else None) codec

    let forValue =
        function
        | Binding.CaseNew _ -> caseNew
        | Binding.Case _ -> caseExisting
        | Binding.CasePayment _ -> casePayment
        | Binding.CaseErase _ -> caseErase
        | Binding.Work _ -> work
        | Binding.ReminderNew _ -> reminderNew
        | Binding.Reminder _ -> reminder
        | Binding.NoteNew _ -> noteNew
        | Binding.Note _ -> note
        | Binding.PeopleNew _ -> peopleNew
        | Binding.People _ -> people

    let matches declaration value =
        let expected = forDeclaration declaration
        let actual = forValue value

        if expected.Schema <> actual.Schema then
            false
        else
            try
                actual.Write value |> actual.Validate "$" |> Result.isOk
            with :? System.ArgumentException ->
                false

    let binding =
        choice
            "Binding"
            [
                select caseNew (function
                    | Binding.CaseNew _ -> true
                    | _ -> false)
                select caseExisting (function
                    | Binding.Case _ -> true
                    | _ -> false)
                select casePayment (function
                    | Binding.CasePayment _ -> true
                    | _ -> false)
                select caseErase (function
                    | Binding.CaseErase _ -> true
                    | _ -> false)
                select work (function
                    | Binding.Work _ -> true
                    | _ -> false)
                select reminderNew (function
                    | Binding.ReminderNew _ -> true
                    | _ -> false)
                select reminder (function
                    | Binding.Reminder _ -> true
                    | _ -> false)
                select noteNew (function
                    | Binding.NoteNew _ -> true
                    | _ -> false)
                select note (function
                    | Binding.Note _ -> true
                    | _ -> false)
                select peopleNew (function
                    | Binding.PeopleNew _ -> true
                    | _ -> false)
                select people (function
                    | Binding.People _ -> true
                    | _ -> false)
            ]
