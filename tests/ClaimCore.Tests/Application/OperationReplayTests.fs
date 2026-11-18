module ClaimCore.Tests.OperationReplayTests

open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Tests.PropertyHarness
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.RecordFormat
open ClaimCore.Tests.FoldRecords

let private grant role =
    inputs
        [
            input InputTarget.Role role
            TaskInputId.Scope, InputValue.Text "INSTALLATION"
        ]

let private revise value = Revision.create value |> Option.get

let private person number =
    ActorId.ofOperationId (operation number)

let private byAuthority authority (record: RetainedOperation) =
    let header =
        { record.Root.Header with
            Authority = authority
        }

    let changes =
        record.Changes
        |> List.map (fun change ->
            { change with
                Stamp =
                    { change.Stamp with
                        Authority = authority
                    }
            })

    RetainedOperation.create
        header
        record.Root.Structure.Task
        record.Root.Structure.Binding
        (record.Inputs |> Option.get)
        changes
        (fun () -> Array.create 32 199uy)
    |> required

let private executor id record = byAuthority (Authority.Actor id) record

let private checkReplay steps =
    let ownerRecord, one = owner ()

    let registered, two =
        build
            one
            2
            principal
            "people.register"
            (Binding.PeopleNew Revision.zero)
            (registration "generated-person")

    let opened, three =
        build
            two
            3
            principal
            "case.open"
            (Binding.CaseNew Revision.zero)
            (opening "GENERATED-REPLAY")

    let target = person 2
    let caseId = CaseId.ofOperationId (operation 3)
    let mutable state = three
    let mutable records = [ ownerRecord; registered; opened ]
    let mutable name = Some "generated-person"
    let mutable enabled = true
    let mutable granted = false
    let mutable closed = false
    let mutable personRevision = 1L
    let mutable caseRevision = 1L
    let mutable notes = 0

    for index, step in List.indexed steps do
        let number = index + 10

        let task, binding, values =
            match step with
            | 0 ->
                name <- Some(sprintf "Replacement %d" number)

                "people.setProfile",
                Binding.People(target, revise personRevision),
                profile name.Value
            | 1 ->
                name <- None
                "people.eraseProfile", Binding.People(target, revise personRevision), inputs []
            | 2 ->
                granted <- not granted

                (if granted then "people.grant" else "people.revoke"),
                Binding.People(target, revise personRevision),
                grant "CASE_READER"
            | 3 ->
                enabled <- not enabled

                (if enabled then "people.enable" else "people.disable"),
                Binding.People(target, revise personRevision),
                inputs []
            | 4 ->
                closed <- not closed

                (if closed then "case.close" else "case.reopen"),
                Binding.Case(CaseSelector.Id caseId, revise caseRevision),
                inputs []
            | _ ->
                notes <- notes + 1

                "note.add",
                Binding.NoteNew(CaseSelector.Id caseId),
                inputs [ input InputTarget.NoteText (sprintf "Exact note %d" number) ]

        let record, next = build state number principal task binding values

        for owner in record.Changes |> List.choose _.Erases do
            records <-
                records
                |> List.map (fun prior ->
                    if
                        prior.Root.Structure.Compartments
                        |> List.exists (fun compartment -> compartment.Descriptor.Owner = owner)
                    then
                        clear prior
                    else
                        prior)

        records <- records @ [ record ]

        if step < 4 then
            personRevision <- personRevision + 1L
        elif step = 4 then
            caseRevision <- caseRevision + 1L

        let replayed = scan records |> required
        Fold.complete replayed |> required |> ignore

        Expect.equal
            replayed.People
            next.People
            "Application replay re-decides accepted Person prefixes"

        Expect.equal
            replayed.Families
            next.Families
            "Application replay re-decides accepted CaseFamily prefixes"

        let current = replayed.People[target]
        Expect.equal current.Actor.Enabled enabled "Independent enablement model"

        Expect.equal
            (current.Actor.Profile
             |> Option.map (fun profile -> PartyName.format profile.DisplayName))
            name
            "Independent profile presence/replacement model"

        Expect.equal
            (current.Grants |> Map.keys |> Seq.toList)
            (if granted then
                 [
                     {
                         ActorGrant.Role = Role.CaseReader
                         Scope = Scope.Installation
                     }
                 ]
             else
                 [])
            "Independent grant model"

        let family = replayed.Families[caseId]

        Expect.equal family.Case.Revision (revise caseRevision) "Independent case revision model"

        Expect.equal
            family.Case.Value.Status
            (if closed then CaseStatus.Closed else CaseStatus.Opened)
            "Independent case lifecycle model"

        Expect.equal family.Notes.Count notes "Independent note identities"
        state <- next

    true

let private generatedReplay =
    property {
        let! steps =
            Gen.list (Range.constant 1 (sequenceMaximum ())) (Gen.int32 (Range.constant 0 5))

        return checkReplay steps
    }

[<Tests>]
let tests =
    testList
        "operation root replay"
        [
            testCase
                "generated Person and CaseFamily accepted sequences replay through the application fold"
                (fun () -> run "aggregate application replay" generatedReplay)
            testCase
                "application replay rejects a coherent root with a corrupt Person effect"
                (fun () ->
                    let first, one = owner ()

                    let registered, two =
                        build
                            one
                            2
                            principal
                            "people.register"
                            (Binding.PeopleNew Revision.zero)
                            (registration "corrupt-person")

                    let corrupt, _ =
                        buildWith
                            (List.map (fun change ->
                                match change.Value with
                                | PartValue.Person value ->
                                    { change with
                                        Value = PartValue.Person { value with Enabled = false }
                                    }
                                | _ -> change))
                            two
                            3
                            principal
                            "people.setProfile"
                            (Binding.People(person 2, Revision.first))
                            (profile "Replacement")

                    Expect.equal
                        (scan [ first; registered; corrupt ])
                        (Error FoldProblem.Decision)
                        "Valid hashes do not legitimize a wrong accepted effect")
            testCase
                "generic root restoration does not replace reviewed-version verification"
                (fun () ->
                    let first, one = owner ()

                    let opened, two =
                        build
                            one
                            2
                            principal
                            "case.open"
                            (Binding.CaseNew Revision.zero)
                            (opening "REVIEWED-BOUND")

                    let parent = CaseId.ofOperationId (operation 2)

                    let closed, _ =
                        build
                            two
                            3
                            principal
                            "case.close"
                            (Binding.Case(CaseSelector.Id parent, Revision.first))
                            InputMap.empty

                    let terminal = Revision.create Revision.Maximum |> Option.get

                    let forged =
                        RetainedOperation.create
                            closed.Root.Header
                            closed.Root.Structure.Task
                            (Binding.Case(CaseSelector.Id parent, terminal))
                            closed.Inputs.Value
                            closed.Changes
                            (fun () -> Array.create 32 44uy)
                        |> required

                    Expect.isOk (scan [ first; opened; closed ]) "Genuine reviewed version"

                    Expect.equal
                        (scan [ first; opened; forged ] |> Result.map ignore)
                        (Error FoldProblem.Decision)
                        "A representable binding with a valid hash cannot authorize a different reviewed version")
            testCase
                "child replay rejects validly committed values unrelated to the authored task"
                (fun () ->
                    let first, one = owner ()

                    let opened, two =
                        build
                            one
                            2
                            principal
                            "case.open"
                            (Binding.CaseNew Revision.zero)
                            (opening "CHILD-REPLAY")

                    let parent = CaseId.ofOperationId (operation 2)
                    let target = CaseSelector.Id parent

                    let work, three =
                        build
                            two
                            3
                            principal
                            "work.update"
                            (Binding.Work(target, Revision.zero))
                            (fields [ BusinessField.HandlingSummary, "Exact summary" ])

                    let reminder, four =
                        build
                            three
                            4
                            principal
                            "reminder.create"
                            (Binding.ReminderNew target)
                            (inputs
                                [
                                    input InputTarget.ReminderAction "Exact action"
                                    input InputTarget.ReminderDueDate "2026-11-17"
                                ])

                    let completed, five =
                        build
                            four
                            5
                            principal
                            "reminder.complete"
                            (Binding.Reminder(
                                target,
                                ReminderId.ofOperationId (operation 4),
                                Revision.first
                            ))
                            InputMap.empty

                    let note, six =
                        build
                            five
                            6
                            principal
                            "note.add"
                            (Binding.NoteNew target)
                            (inputs [ input InputTarget.NoteText "Exact first note" ])

                    let other, seven =
                        build
                            six
                            7
                            principal
                            "note.add"
                            (Binding.NoteNew target)
                            (inputs [ input InputTarget.NoteText "Other note" ])

                    let removed, _ =
                        build
                            seven
                            8
                            principal
                            "note.remove"
                            (Binding.Note(target, NoteId.ofOperationId (operation 6)))
                            InputMap.empty

                    let forge (record: RetainedOperation) mutate =
                        RetainedOperation.create
                            record.Root.Header
                            record.Root.Structure.Task
                            record.Root.Structure.Binding
                            record.Inputs.Value
                            (record.Changes |> List.map mutate)
                            (fun () -> Array.create 32 77uy)
                        |> required

                    let forgedWork =
                        forge work (fun part ->
                            match part.Value with
                            | PartValue.Work(Some value) ->
                                { part with
                                    Value =
                                        PartValue.Work(
                                            Some
                                                { value with
                                                    Summary =
                                                        Some(
                                                            HandlingSummary.parse "Forged"
                                                            |> required
                                                        )
                                                }
                                        )
                                }
                            | _ -> failtest "Work required")

                    let forgedReminder =
                        forge completed (fun part ->
                            match part.Value with
                            | PartValue.Reminder value ->
                                { part with
                                    Value =
                                        PartValue.Reminder
                                            { value with
                                                Plan =
                                                    value.Plan
                                                    |> Option.map (fun plan ->
                                                        { plan with
                                                            Action =
                                                                ReminderAction.parse "Forged"
                                                                |> required
                                                        })
                                            }
                                }
                            | _ -> failtest "Reminder required")

                    let forgedRemoval =
                        forge removed (fun part ->
                            { part with
                                PartId = OperationId.value (operation 7)
                            })

                    for prefix, original, forged in
                        [
                            [ first; opened ], work, forgedWork
                            [ first; opened; work; reminder ], completed, forgedReminder
                            [ first; opened; work; reminder; completed; note; other ],
                            removed,
                            forgedRemoval
                        ] do
                        Expect.isOk (scan (prefix @ [ original ])) "Original task result passes"

                        Expect.equal
                            (scan (prefix @ [ forged ]) |> Result.map ignore)
                            (Error FoldProblem.Decision)
                            "Valid hashes and individually representable values do not establish causality")
            testCase
                "replay admits assertions against the stored business date across midnight"
                (fun () ->
                    let first, one = owner ()
                    let values = opening "STORED-DATE"

                    let submitted day =
                        InputMap.create
                            (values.Values
                             |> Map.add
                                 (TaskInputId.Target(InputTarget.Field BusinessField.IncidentDate))
                                 (InputValue.Text day)
                             |> Map.add
                                 (TaskInputId.Target(InputTarget.Field BusinessField.ReceivedDate))
                                 (InputValue.Text day))
                            Map.empty
                        |> Option.get

                    let original, _ =
                        build
                            one
                            2
                            principal
                            "case.open"
                            (Binding.CaseNew Revision.zero)
                            (submitted "2026-11-16")

                    let at = System.DateTimeOffset(2026, 11, 17, 0, 0, 1, System.TimeSpan.Zero)

                    let header =
                        { original.Root.Header with
                            RecordedAt = at
                        }

                    let changes =
                        original.Changes
                        |> List.map (fun part ->
                            { part with
                                Stamp = { part.Stamp with RecordedAt = at }
                            })

                    let record inputs =
                        RetainedOperation.create
                            header
                            original.Root.Structure.Task
                            original.Root.Structure.Binding
                            inputs
                            changes
                            (fun () -> Array.create 32 42uy)
                        |> required

                    Expect.isOk
                        (scan [ first; record (submitted "2026-11-16") ])
                        "Stored decision date positive control"

                    Expect.equal
                        (scan [ first; record (submitted "2026-11-17") ] |> Result.map ignore)
                        (Error FoldProblem.Decision)
                        "Recorded timestamp cannot authorize tomorrow's assertion")
            testCase
                "replay rejects changed individual payment amounts despite unchanged case totals"
                (fun () ->
                    let first, one = owner ()

                    let opened, two =
                        build
                            one
                            2
                            principal
                            "case.open"
                            (Binding.CaseNew Revision.zero)
                            (opening "PAYMENT-TAMPER")

                    let id = CaseId.ofOperationId (operation 2)

                    let binding revision =
                        Binding.Case(CaseSelector.Id id, revise revision)

                    let decided, three =
                        build
                            two
                            3
                            principal
                            "case.decide"
                            (binding 1L)
                            (fields
                                [
                                    BusinessField.PaymentDecisionDate, "2026-01-03"
                                    BusinessField.PayableAmount, "750"
                                    BusinessField.PayableCurrency, "EUR"
                                ])

                    let values =
                        fields
                            [
                                BusinessField.PaymentDate, "2026-01-04"
                                BusinessField.PaidAmount, "100"
                                BusinessField.PaidCurrency, "EUR"
                            ]

                    let original, _ =
                        build three 4 principal "case.recordPayment" (binding 2L) values

                    let changes =
                        original.Changes
                        |> List.map (fun part ->
                            match part.Value with
                            | PartValue.Payment payment ->
                                let entry = payment.Entry.Value

                                let changed =
                                    PaymentEntry.create
                                        (PaymentEntry.id entry)
                                        (PaymentEntry.direction entry)
                                        (Money.create
                                            (Amount.parse "99" |> required)
                                            (PaymentEntry.money entry |> Money.currencyValue))
                                        (PaymentEntry.date entry)
                                        (PaymentEntry.reference entry)
                                        (PaymentEntry.payee entry)
                                    |> required

                                { part with
                                    Value =
                                        PartValue.Payment { payment with Entry = Some changed }
                                }
                            | _ -> part)

                    let forged =
                        RetainedOperation.create
                            original.Root.Header
                            original.Root.Structure.Task
                            original.Root.Structure.Binding
                            values
                            changes
                            (fun () -> Array.create 32 43uy)
                        |> required

                    Expect.isOk (scan [ first; opened; decided; original ]) "Exact payment control"

                    Expect.equal
                        (scan [ first; opened; decided; forged ] |> Result.map ignore)
                        (Error FoldProblem.Decision)
                        "The case part is unchanged; individual amount still must match")
            testCase
                "erased-profile regrant replay retains the latest real accepting position"
                (fun () ->
                    let first, one = owner ()

                    let registered, two =
                        build
                            one
                            2
                            principal
                            "people.register"
                            (Binding.PeopleNew Revision.zero)
                            (registration "regranted")

                    let granted, three =
                        build
                            two
                            3
                            principal
                            "people.grant"
                            (Binding.People(person 2, Revision.first))
                            (grant "CASE_READER")

                    let reaffirmed, four =
                        build
                            three
                            4
                            principal
                            "people.grant"
                            (Binding.People(person 2, revise 2L))
                            (grant "CASE_READER")

                    let erased, _ =
                        build
                            four
                            5
                            principal
                            "people.eraseProfile"
                            (Binding.People(person 2, revise 3L))
                            InputMap.empty

                    let after =
                        scan [ first; clear registered; clear granted; clear reaffirmed; erased ]
                        |> required
                        |> Fold.complete
                        |> required

                    let key =
                        {
                            Role = Role.CaseReader
                            Scope = Scope.Installation
                        }

                    Expect.equal
                        after.People[person 2].Grants[key]
                        (EffectPosition.create (Sequence.create 4L |> Option.get) 0 |> Option.get)
                        "The current grant belongs to its reaffirming person part")
            testCase "operator authority cannot replay a family operation" (fun () ->
                let provisioned, state = owner ()

                let opened, _ =
                    build
                        state
                        2
                        principal
                        "case.open"
                        (Binding.CaseNew Revision.zero)
                        (opening "OPERATOR-FAMILY")

                Expect.isOk (scan [ provisioned; opened ]) "The actor-attributed control replays"

                Expect.equal
                    (scan [ provisioned; byAuthority Authority.Operator opened ])
                    (Error(FoldProblem.Executor ExecutorIssue.ActorNotEnabled))
                    "The fold refuses the rehashed family operation with operator authority")
            testCase "bootstrap records the profile and every role in one person part" (fun () ->
                let record, _ = owner ()
                let state = scan [ record ] |> required

                Expect.equal
                    state.Head.Sequence
                    Sequence.first
                    "One operation extends the head once"

                Expect.equal record.Root.Header.Authority Authority.Operator "Operator attribution"
                Expect.equal record.Changes.Length 1 "One accepted PERSON part"
                Expect.equal state.People.Count 1 "One principal"
                let current = state.People[person 1]

                Expect.equal
                    (current.Actor.Profile |> Option.map (_.DisplayName >> PartyName.format))
                    (Some "owner")
                    "Profile value"

                Expect.equal
                    (current.Grants |> Map.keys |> Seq.map _.Role |> Set.ofSeq)
                    (set [ Role.Owner; Role.CaseHandler; Role.PaymentRecorder; Role.DataSteward ])
                    "Explicit and implicit roles"

                for position in current.Grants |> Map.values do
                    Expect.equal
                        position
                        (EffectPosition.create Sequence.first 0 |> Option.get)
                        "All assignments belong to the real person part"

                Expect.isOk (Fold.complete state) "No erasure is needed")
            testCase
                "profile erasure preserves admitted principal and grants, then permits a replacement profile"
                (fun () ->
                    let first, one = owner ()
                    let values = registration "admitted"

                    let rows =
                        [ "AUDITOR"; "CASE_HANDLER" ] |> List.map (fun role -> (grant role).Values)

                    let values =
                        InputMap.create
                            values.Values
                            (Map.ofList [ TaskCollectionId.PeopleGrants, rows ])
                        |> Option.get

                    let admitted, two =
                        build
                            one
                            2
                            principal
                            "people.admit"
                            (Binding.PeopleNew Revision.zero)
                            values

                    let erased, three =
                        build
                            two
                            3
                            principal
                            "people.eraseProfile"
                            (Binding.People(person 2, Revision.first))
                            InputMap.empty

                    let prefix =
                        scan [ first; clear admitted; erased ]
                        |> required
                        |> Fold.complete
                        |> required

                    let current = prefix.People[person 2]

                    Expect.equal
                        current.Actor.Principal
                        (PrincipalKey.human "https://issuer.example/" "admitted" |> required)
                        "Retained principal"

                    Expect.isNone current.Actor.Profile "Private profile was cleared"

                    Expect.equal
                        (current.Grants |> Map.keys |> Seq.map _.Role |> Set.ofSeq)
                        (set [ Role.Auditor; Role.CaseHandler ])
                        "Retained grants"

                    for position in current.Grants |> Map.values do
                        Expect.equal
                            position
                            (EffectPosition.create (Sequence.create 2L |> Option.get) 0
                             |> Option.get)
                            "No invented grant coordinates"

                    let replacement, _ =
                        build
                            three
                            4
                            principal
                            "people.setProfile"
                            (Binding.People(person 2, revise 2L))
                            (profile "Replacement")

                    let after =
                        scan [ first; clear admitted; erased; replacement ]
                        |> required
                        |> Fold.complete
                        |> required

                    Expect.equal
                        (after.People[person 2].Actor.Profile
                         |> Option.map (_.DisplayName >> PartyName.format))
                        (Some "Replacement")
                        "New profile survives the older erasure"

                    Expect.equal
                        after.People[person 2].Grants
                        current.Grants
                        "Profile replacement preserves accepting positions")
            testCase "an erased self-admission cannot authorize its own command" (fun () ->
                let first, one = owner ()
                let values = registration "untrusted"

                let values =
                    InputMap.create
                        values.Values
                        (Map.ofList [ TaskCollectionId.PeopleGrants, [ (grant "OWNER").Values ] ])
                    |> Option.get

                let admitted, _ =
                    build one 2 principal "people.admit" (Binding.PeopleNew Revision.zero) values

                let forged = admitted |> executor (person 2) |> clear
                let before = scan [ first ] |> required

                Expect.equal
                    (Fold.append installation before forged)
                    (Error(FoldProblem.Executor ExecutorIssue.ActorNotEnabled))
                    "Authorization observes earlier people only")
            testCase "public mutations cannot hide behind erased profile inputs" (fun () ->
                let second =
                    PrincipalKey.human "https://issuer.example/" "second-owner" |> required

                let first, one = owner ()

                let registered, two =
                    build
                        one
                        2
                        principal
                        "people.register"
                        (Binding.PeopleNew Revision.zero)
                        (registration "second-owner")

                let granted, three =
                    build
                        two
                        3
                        principal
                        "people.grant"
                        (Binding.People(person 2, Revision.first))
                        (grant "OWNER")

                let binding = Binding.People(person 1, Revision.first)

                let forge (changes: PartChange list) =
                    changes
                    |> List.map (fun change ->
                        match change.Value with
                        | PartValue.Person value ->
                            { change with
                                Value =
                                    PartValue.Person
                                        { value with
                                            Enabled = false
                                            Grants =
                                                [
                                                    {
                                                        Grant =
                                                            {
                                                                Role = Role.Owner
                                                                Scope = Scope.Installation
                                                            }
                                                        Active = false
                                                    }
                                                ]
                                        }
                            }
                        | _ -> failtest "Expected the person part")

                let forged, wrong =
                    buildWith
                        forge
                        three
                        4
                        second
                        "people.setProfile"
                        binding
                        (profile "Changed name")

                let erase, _ =
                    build
                        wrong
                        5
                        second
                        "people.eraseProfile"
                        (Binding.People(person 1, revise 2L))
                        InputMap.empty

                let before = scan [ clear first; registered; granted ] |> required

                Expect.isTrue
                    before.People[person 1].Actor.Enabled
                    "The valid prefix retains the public account"

                Expect.equal
                    (Fold.append installation before (clear forged))
                    (Error FoldProblem.Decision)
                    "The profile task did not decide a disable or role revocation"

                Expect.equal
                    (scan [ clear first; registered; granted; clear forged; erase ])
                    (Error FoldProblem.Decision)
                    "Later erasure cannot conceal the forgery"

                let proper, valid =
                    build three 4 second "people.setProfile" binding (profile "Changed name")

                let properErase, _ =
                    build
                        valid
                        5
                        second
                        "people.eraseProfile"
                        (Binding.People(person 1, revise 2L))
                        InputMap.empty

                let after =
                    scan [ clear first; registered; granted; clear proper; properErase ]
                    |> required
                    |> Fold.complete
                    |> required

                Expect.isTrue
                    after.People[person 1].Actor.Enabled
                    "The genuine erased update verifies"

                Expect.isTrue
                    (Map.containsKey
                        {
                            Role = Role.Owner
                            Scope = Scope.Installation
                        }
                        after.People[person 1].Grants)
                    "Owner rights survive")
            testCase
                "requester findings distinguish missing, human and owner requirements"
                (fun () ->
                    let first, one = owner ()

                    let registered, two =
                        build
                            one
                            2
                            principal
                            "people.register"
                            (Binding.PeopleNew Revision.zero)
                            (registration "reader")

                    let changed, _ =
                        build
                            two
                            3
                            principal
                            "people.setProfile"
                            (Binding.People(person 1, Revision.first))
                            (profile "Changed")

                    let prefix =
                        Replay.append installation None (Replay.initial None) first |> required

                    let prefix = Replay.append installation None prefix registered |> required

                    let requireFinding finding record =
                        match Replay.append installation None prefix record with
                        | Error issue ->
                            Expect.equal issue.Finding finding "Specific requester finding"
                        | Ok _ -> failtest "Forged executor was accepted"

                    requireFinding
                        VerificationFinding.ActorNotEnabled
                        (executor (person 99) changed)

                    requireFinding VerificationFinding.OwnerRequired (executor (person 2) changed)
                    let serviceInputs = registration "service"

                    let serviceInputs =
                        InputMap.create
                            (Map.add
                                (TaskInputId.Target InputTarget.PrincipalKind)
                                (InputValue.Text "SERVICE")
                                serviceInputs.Values)
                            Map.empty
                        |> Option.get

                    let serviceRecord, serviceState =
                        build
                            one
                            2
                            principal
                            "people.register"
                            (Binding.PeopleNew Revision.zero)
                            serviceInputs

                    let changed, _ =
                        build
                            serviceState
                            3
                            principal
                            "people.setProfile"
                            (Binding.People(person 1, Revision.first))
                            (profile "Changed")

                    let servicePrefix =
                        Replay.append installation None (Replay.initial None) first
                        |> required
                        |> fun state ->
                            Replay.append installation None state serviceRecord |> required

                    match
                        Replay.append installation None servicePrefix (executor (person 2) changed)
                    with
                    | Error issue ->
                        Expect.equal
                            issue.Finding
                            VerificationFinding.HumanRequired
                            "A valid service account is not a human requester"
                    | Ok _ -> failtest "The service account executed a human-only task")
            testCase
                "erased family replay retains payment membership, removal and actual part positions"
                (fun () ->
                    let first, one = owner ()

                    let opened, two =
                        build
                            one
                            2
                            principal
                            "case.open"
                            (Binding.CaseNew Revision.zero)
                            (opening "REPLAY-PAYMENTS")

                    let id = CaseId.ofOperationId (operation 2)

                    let binding revision =
                        Binding.Case(CaseSelector.Id id, revise revision)

                    let decided, three =
                        build
                            two
                            3
                            principal
                            "case.decide"
                            (binding 1L)
                            (fields
                                [
                                    BusinessField.PaymentDecisionDate, "2026-01-03"
                                    BusinessField.PayableAmount, "750"
                                    BusinessField.PayableCurrency, "EUR"
                                ])

                    let paid, four =
                        build
                            three
                            4
                            principal
                            "case.recordPayment"
                            (binding 2L)
                            (fields
                                [
                                    BusinessField.PaymentDate, "2026-01-04"
                                    BusinessField.PaidAmount, "100"
                                    BusinessField.PaidCurrency, "EUR"
                                ])

                    let paidAgain, five =
                        build
                            four
                            5
                            principal
                            "case.recordPayment"
                            (binding 3L)
                            (fields
                                [
                                    BusinessField.PaymentDate, "2026-01-05"
                                    BusinessField.PaidAmount, "200"
                                    BusinessField.PaidCurrency, "EUR"
                                ])

                    let removed, six =
                        build
                            five
                            6
                            principal
                            "case.removePayment"
                            (Binding.CasePayment(CaseSelector.Id id, revise 4L, operation 4))
                            InputMap.empty

                    let work, seven =
                        build
                            six
                            7
                            principal
                            "work.update"
                            (Binding.Work(CaseSelector.Id id, Revision.zero))
                            (fields [ BusinessField.HandlingSummary, "Private work" ])

                    let note, eight =
                        build
                            seven
                            8
                            principal
                            "note.add"
                            (Binding.NoteNew(CaseSelector.Id id))
                            (inputs [ input InputTarget.NoteText "Private note" ])

                    let reminder, nine =
                        build
                            eight
                            9
                            principal
                            "reminder.create"
                            (Binding.ReminderNew(CaseSelector.Id id))
                            (inputs
                                [
                                    input InputTarget.ReminderAction "Follow up"
                                    input InputTarget.ReminderDueDate "2026-11-17"
                                ])

                    let family = nine.Families[id]

                    let erased, _ =
                        build
                            nine
                            10
                            principal
                            "case.erase"
                            (Binding.CaseErase(
                                CaseSelector.Id id,
                                family.Case.Revision,
                                family.Sequence
                            ))
                            InputMap.empty

                    let records =
                        [ opened; decided; paid; paidAgain; removed; work; note; reminder ]
                        |> List.map clear

                    let after =
                        scan (first :: records @ [ erased ])
                        |> required
                        |> Fold.complete
                        |> required

                    let family = after.Families[id]

                    Expect.equal
                        family.Reference
                        (CaseReference.parse "REPLAY-PAYMENTS" |> required)
                        "Public reference"

                    Expect.equal family.Case.Value.Lifecycle Lifecycle.Erased "Erasure state"

                    Expect.equal
                        (Revision.value family.Case.Revision)
                        6L
                        "Only case parts advanced case revision"

                    Expect.equal family.Payments.Count 2 "Both payment identities survive"
                    Expect.isTrue family.Payments[operation 4].Value.Removed "Removal survives"

                    Expect.isFalse
                        family.Payments[operation 5].Value.Removed
                        "Other payment remains structurally active"

                    Expect.isTrue
                        (family.Payments |> Map.forall (fun _ part -> part.Value.Entry.IsNone))
                        "Payment values unavailable"

                    Expect.equal
                        (after.Parts[(PartKind.Payment, OperationId.value (operation 4))]
                            .Stamp.Position
                         |> EffectPosition.index)
                        0
                        "Payment part position"

                    Expect.equal
                        (paid.Changes
                         |> List.find (fun part -> PartValue.kind part.Value = PartKind.Case)
                         |> _.Stamp.Position
                         |> EffectPosition.index)
                        1
                        "Case effect has its own position"

                    Expect.isNone (family.Work |> Option.bind _.Value) "Work content unavailable"

                    Expect.isTrue
                        (family.Reminders |> Map.forall (fun _ part -> part.Value.Plan.IsNone))
                        "Reminder content unavailable"

                    match after.Parts[(PartKind.Note, OperationId.value (operation 8))].Value with
                    | PartValue.Note value -> Expect.isNone value.Text "Note content unavailable"
                    | _ -> failtest "Expected the accepted note part")
        ]
