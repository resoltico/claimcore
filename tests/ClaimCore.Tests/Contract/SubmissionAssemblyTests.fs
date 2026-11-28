module ClaimCore.Tests.SubmissionAssemblyTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Wire

let private declaration name =
    (TaskDeclarations.describe name).Ledger |> Option.get

let private identifier = Guid.Parse "10000000-0000-7000-8000-000000000001"
let private selector = CaseSelector.Id(CaseId.create identifier |> Option.get)
let private revision = Revision.create 3L |> Option.get
let private token = "synthetic-review"

[<Tests>]
let tests =
    testList
        "shared submission assembly"
        [
            testCase
                "offered controls construct all declared shapes from explicit observed facts"
                (fun () ->
                    let expected =
                        [
                            "case.open", Binding.CaseNew Revision.zero
                            "case.close", Binding.Case(selector, revision)
                            "case.removePayment",
                            Binding.CasePayment(
                                selector,
                                revision,
                                OperationId.create identifier |> Option.get
                            )
                            "case.erase",
                            Binding.CaseErase(selector, revision, ErasureReview.Token token)
                            "work.update", Binding.Work(selector, revision)
                            "note.add", Binding.NoteNew selector
                            "note.remove",
                            Binding.Note(selector, NoteId.create identifier |> Option.get)
                            "reminder.create", Binding.ReminderNew selector
                            "reminder.update",
                            Binding.Reminder(
                                selector,
                                ReminderId.create identifier |> Option.get,
                                revision
                            )
                            "people.register", Binding.PeopleNew Revision.zero
                            "people.enable",
                            Binding.People(ActorId.create identifier |> Option.get, revision)
                        ]

                    for task, expected in expected do
                        Expect.equal
                            (SubmissionAssembly.offeredBinding
                                (declaration task)
                                (Some selector)
                                (Some revision)
                                (Some identifier)
                                (Some token))
                            expected
                            "No refreshed or implicit basis")
            testCase
                "revision acquisition uses exact creation and subject-specific semantic bounds"
                (fun () ->
                    for task, input, minimum, maximum in
                        [
                            "case.open", InputTarget.ExpectedRevision, 0L, 0L
                            "people.register", InputTarget.ExpectedRevision, 0L, 0L
                            "case.close", InputTarget.ExpectedRevision, 1L, Revision.Maximum
                            "people.enable", InputTarget.ExpectedRevision, 1L, Revision.Maximum
                            "reminder.update",
                            InputTarget.ExpectedReminderRevision,
                            1L,
                            Revision.Maximum
                            "work.update", InputTarget.ExpectedWorkRevision, 0L, Revision.Maximum
                        ] do
                        Expect.equal
                            (SubmissionDeclarations.integerBounds
                                (declaration task)
                                (TaskInputId.Target input))
                            (minimum, maximum)
                            "Owner bounds")
            testCase
                "malformed or competing retained IDs never become reference selectors"
                (fun () ->
                    let id = SubmissionDeclarations.caseIdInput
                    let reference = SubmissionDeclarations.caseReferenceInput

                    for values in
                        [
                            Map.ofList [ id, SubmissionValue.Text "malformed" ]
                            Map.ofList [ id, SubmissionValue.Text "" ]
                            Map.ofList [ id, SubmissionValue.Clear ]
                            Map.ofList [ id, SubmissionValue.Text(Guid.Empty.ToString "D") ]
                            Map.ofList
                                [
                                    id, SubmissionValue.Text(identifier.ToString "D")
                                    reference, SubmissionValue.Text "AUTHORED"
                                ]
                        ] do
                        Expect.isError
                            (SubmissionAssembly.caseSelection values)
                            "No selector fallback"

                    Expect.equal
                        (SubmissionAssembly.caseSelection (
                            Map.ofList [ reference, SubmissionValue.Text(identifier.ToString "D") ]
                        ))
                        (Ok(CaseSelector.Reference(identifier.ToString "D")))
                        "UUID-shaped authored reference stays a reference")
            testCase
                "closed scope states refuse every conflicting installation and duplicate selection"
                (fun () ->
                    for selected in
                        [
                            None
                            Some SubmissionDeclarations.caseScope
                            Some SubmissionDeclarations.installationScope
                            Some "INVALID"
                        ] do
                        Expect.isNone
                            (SubmissionDeclarations.scopeRule selected true true)
                            "Two alternatives refuse"

                    for hasId, hasReference in [ true, false; false, true ] do
                        Expect.isNone
                            (SubmissionDeclarations.scopeRule
                                (Some SubmissionDeclarations.installationScope)
                                hasId
                                hasReference)
                            "Explicit installation cannot erase a case selection"

                    Expect.equal
                        ((SubmissionDeclarations.scopeRule None true false |> Option.get).Input)
                        (Some SubmissionDeclarations.caseIdInput)
                        "Presence owns selection, even before syntax admission"

                    let invalid =
                        Map.ofList [ SubmissionDeclarations.caseIdInput, SubmissionValue.Clear ]

                    Expect.equal
                        (SubmissionAssembly.scope invalid
                         |> Map.find SubmissionDeclarations.scopeInput)
                        (SubmissionValue.Text SubmissionDeclarations.caseScope)
                        "Malformed case remains case for visible refusal")
            testCase
                "authored atoms and explicit defaults retain omission null false zero and empty text"
                (fun () ->
                    let work = declaration "work.update"

                    let summary =
                        TaskInputId.Target(InputTarget.Field BusinessField.HandlingSummary)

                    for value in
                        [
                            SubmissionValue.Clear
                            SubmissionValue.Boolean false
                            SubmissionValue.Integer 0L
                            SubmissionValue.Text ""
                        ] do
                        let authored = Map.ofList [ summary, value ]

                        Expect.equal
                            (SubmissionAssembly.merge [ summary ] Map.empty authored)
                            authored
                            "No truthiness or null coalescing"

                    Expect.isEmpty
                        (SubmissionAssembly.merge [ summary ] Map.empty Map.empty)
                        "Omission stays omitted"

                    Expect.isEmpty
                        (SubmissionAssembly.fixedInputs
                            work
                            []
                            (Map.ofList [ summary, SubmissionValue.Text "display" ]))
                        "Noneditable update hint stays dormant"

                    let correction = declaration "case.correct"
                    let group = TaskInputId.Target(InputTarget.Group RecordGroup.Registration)
                    let explicit = Map.ofList [ group, SubmissionValue.Text "UPDATE" ]

                    Expect.equal
                        (SubmissionAssembly.withDefaults correction explicit |> Map.find group)
                        (SubmissionValue.Text "UPDATE")
                        "Explicit authored default selection wins")
            testCase
                "invalid bound operation control returns a codec problem without throwing"
                (fun () ->
                    let bytes =
                        Encoding.UTF8.GetBytes
                            """{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"ASSEMBLY"},"expectedRevision":0}"""

                    let result =
                        Codec.decode
                            Int32.MaxValue
                            (OperationSubmissionCodecs.binding (TaskId.ofLiteral "case.close"))
                            bytes

                    Expect.isError result "Positive reviewed case basis is structurally enforced")
        ]
