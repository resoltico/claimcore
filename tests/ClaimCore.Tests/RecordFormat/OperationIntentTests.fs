module ClaimCore.Tests.OperationIntentTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.RecordFormat
open ClaimCore.Tests.RecordBuilders

module Tasks = ClaimCore.Tests.TaskBuilders

let private declaration token =
    ClaimCore.Domain.Tasks.Registry.findDeclaration (TaskId.ofLiteral token)
    |> Option.get

let private parent = (Tasks.family ()).Id

let private resolve =
    function
    | CaseSelector.Reference _ -> Some parent
    | CaseSelector.Id id -> Some id

let private hash task binding inputs =
    OperationIntent.hash (declaration task) binding inputs resolve

[<Tests>]
let tests =
    testList
        "operation input identity"
        [
            testCase "case identity normalizes decimal and resolved reference spelling" (fun () ->
                let first =
                    hash
                        "case.decide"
                        (Binding.Case(CaseSelector.Reference "Case-One", Revision.first))
                        (Tasks.decisionInputs "2026-01-02" "10.0000" "EUR")

                let same =
                    hash
                        "case.decide"
                        (Binding.Case(CaseSelector.Id parent, Revision.first))
                        (Tasks.decisionInputs "2026-01-02" "10" "EUR")

                Expect.equal first same "Same typed target and business amount"

                Expect.notEqual
                    first
                    (hash
                        "case.decide"
                        (Binding.Case(CaseSelector.Id parent, Revision.first))
                        (Tasks.decisionInputs "2026-01-02" "11" "EUR"))
                    "Changed business input"

                Expect.notEqual
                    first
                    (hash
                        "case.decide"
                        (Binding.Case(
                            CaseSelector.Id(CaseId.ofOperationId (Tasks.operation 99)),
                            Revision.first
                        ))
                        (Tasks.decisionInputs "2026-01-02" "10" "EUR"))
                    "Different target"

                Expect.notEqual
                    first
                    (hash
                        "case.decide"
                        (Binding.Case(CaseSelector.Id parent, Revision.create 2L |> Option.get))
                        (Tasks.decisionInputs "2026-01-02" "10" "EUR"))
                    "Reviewed version is intent")
            testCase
                "recording metadata and private result erasure do not erase a known public close input"
                (fun () ->
                    let state = Tasks.family ()
                    let binding = Tasks.caseBinding state
                    let record = family "case.close" 2 state binding InputMap.empty
                    let expected = Some(hash "case.close" binding InputMap.empty)
                    Expect.equal (OperationIntent.accepted record resolve) expected "Known input"

                    Expect.equal
                        (OperationIntent.accepted (clear record) resolve)
                        expected
                        "Only result content was erased"

                    let header =
                        { record.Root.Header with
                            Sequence = Sequence.successor record.Root.Header.Sequence
                            PreviousHash = record.Root.Hash
                            OperationId = Tasks.operation 99
                            RecordedAt = record.Root.Header.RecordedAt.AddSeconds 1.
                        }

                    let changes =
                        record.Changes
                        |> List.map (fun change ->
                            { change with
                                Stamp =
                                    { change.Stamp with
                                        OperationId = header.OperationId
                                        RecordedAt = header.RecordedAt
                                        Position =
                                            EffectPosition.create header.Sequence 0 |> Option.get
                                    }
                            })

                    let again =
                        RetainedOperation.create
                            header
                            record.Root.Structure.Task
                            binding
                            InputMap.empty
                            changes
                            (fun () -> Array.create 32 99uy)
                        |> required

                    Expect.notEqual
                        again.Root.Hash
                        record.Root.Hash
                        "Recording and compartment context changed"

                    Expect.equal
                        (OperationIntent.accepted again resolve)
                        expected
                        "Intent excludes that context"

                    Expect.notEqual
                        (hash "case.close" binding InputMap.empty)
                        (hash "case.reopen" binding InputMap.empty)
                        "Task identity remains explicit")
            testCase
                "erased authored inputs stay unavailable rather than receiving a fabricated fingerprint"
                (fun () ->
                    let record = caseRecord ()

                    Expect.isSome
                        (OperationIntent.accepted record resolve)
                        "Available complete input"

                    Expect.isNone
                        (OperationIntent.accepted (clear record) resolve)
                        "No unsalted persisted private-input digest")
            testCase
                "grant scopes compare by resolved case identity and preserve the person binding"
                (fun () ->
                    let actor = ActorId.ofOperationId (Tasks.operation 7)
                    let binding = Binding.People(actor, Revision.first)

                    let referenced =
                        Tasks.inputs
                            [
                                Tasks.text InputTarget.Role "CASE_READER"
                                TaskInputId.Scope, InputValue.Text "CASE"
                                Tasks.text
                                    (InputTarget.Field BusinessField.CaseReference)
                                    "Case-One"
                            ]

                    let selected =
                        Tasks.inputs
                            [
                                Tasks.text InputTarget.Role "CASE_READER"
                                TaskInputId.Scope, InputValue.Text "CASE"
                                TaskInputId.CaseId, InputValue.Text(CaseId.format parent)
                            ]

                    Expect.equal
                        (hash "people.grant" binding referenced)
                        (hash "people.grant" binding selected)
                        "Reference alias equals retained ID"

                    Expect.notEqual
                        (hash "people.grant" binding selected)
                        (hash
                            "people.grant"
                            (Binding.People(
                                ActorId.ofOperationId (Tasks.operation 8),
                                Revision.first
                            ))
                            selected)
                        "Another person cannot borrow intent")
            testCase
                "clearable profile contact null equals absence while work clear differs from omission"
                (fun () ->
                    let values = Tasks.inputs Tasks.profileInputs

                    let withoutContact =
                        InputMap.create
                            (Map.remove
                                (TaskInputId.Target InputTarget.ProfileContact)
                                values.Values)
                            Map.empty
                        |> Option.get

                    let nullContact =
                        InputMap.create
                            (Map.add
                                (TaskInputId.Target InputTarget.ProfileContact)
                                InputValue.Null
                                withoutContact.Values)
                            Map.empty
                        |> Option.get

                    Expect.equal
                        (hash
                            "people.setProfile"
                            (Tasks.personBinding (Tasks.person ()))
                            withoutContact)
                        (hash
                            "people.setProfile"
                            (Tasks.personBinding (Tasks.person ()))
                            nullContact)
                        "Clearable complete-profile contact"

                    let binding = Binding.Work(CaseSelector.Id parent, Revision.first)

                    let keep =
                        Tasks.inputs
                            [
                                Tasks.text
                                    (InputTarget.Field BusinessField.ResponsibleActor)
                                    (ActorId.format Tasks.actorId)
                            ]

                    let clear =
                        InputMap.create
                            (Map.add
                                (TaskInputId.Target(
                                    InputTarget.Field BusinessField.HandlingSummary
                                ))
                                InputValue.Null
                                keep.Values)
                            Map.empty
                        |> Option.get

                    Expect.notEqual
                        (hash "work.update" binding keep)
                        (hash "work.update" binding clear)
                        "Explicit clear has a different meaning")
            testCase "collection order is retained in complete authored intent" (fun () ->
                let values = Tasks.peopleInputs Tasks.newPrincipal

                let rows =
                    [
                        (Tasks.grantInputs Role.CaseReader "INSTALLATION").Values
                        (Tasks.grantInputs Role.Auditor "INSTALLATION").Values
                    ]

                let first =
                    InputMap.create
                        values.Values
                        (Map.ofList [ TaskCollectionId.PeopleGrants, rows ])
                    |> Option.get

                let reversed =
                    InputMap.create
                        values.Values
                        (Map.ofList [ TaskCollectionId.PeopleGrants, List.rev rows ])
                    |> Option.get

                Expect.notEqual
                    (hash "people.admit" (Binding.PeopleNew Revision.zero) first)
                    (hash "people.admit" (Binding.PeopleNew Revision.zero) reversed)
                    "Ordinals survive intent normalization")
        ]
