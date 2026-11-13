module ClaimCore.Tests.RetainedOperationTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

let private valid result =
    Result.defaultWith (fun error -> failtestf "Retained fixture: %A" error) result

let private guid number =
    Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)

let private operation number =
    OperationId.create (guid number) |> Option.get

let private parent = CaseId.ofOperationId (operation 1)
let private today = DateOnly(2026, 11, 9)

let private header number people =
    {
        OperationHeader.InstallationId = Guid.Parse "00000000-0000-4000-8000-000000000001"
        Sequence = Sequence.create (int64 number + 10L) |> Option.get
        PreviousHash = EntryHash.zero
        OperationId = operation number
        ActorId = ActorId.create (guid 9) |> Option.get
        RecordedAt = DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)
        BusinessDate = if people then None else Some today
    }

let private nonce () = [| 0uy .. 31uy |]

let private decide number act (before: CaseSnapshot option) =
    let command =
        Case.parse
            {
                OperationId = guid number
                CaseReference = "PRIVATE-1"
                ExpectedRevision =
                    before |> Option.map (_.Revision >> Revision.value) |> Option.defaultValue 0L
                Act = act
            }
        |> valid

    let context = Map.empty

    command, Case.decideSnapshot today context command before |> valid

let private caseOperation number (command: CaseCommand) (decision: CaseDecision) =
    let selected =
        match command.Act with
        | CaseAct.RemovePayment id -> Some id
        | _ -> None

    let scope =
        CommandSkeleton.Case
            {
                CaseId = decision.After.CaseId
                Reference = decision.After.Reference
                ExpectedRevision = command.ExpectedRevision
                Act = CaseAct.kind command.Act
                SelectedPayment = selected
            }

    RetainedOperation.create
        (header number false)
        scope
        (OperationValues.Case
            {
                Command = command
                Decision = decision
            })
        nonce
    |> valid

let private opened () =
    let command, decision = decide 1 (CaseAct.Open Inputs.registration) None
    caseOperation 1 command decision

let private body (root: OperationRoot) =
    root.Structure.Compartments
    |> List.exactlyOne
    |> Compartment.content
    |> Option.get
    |> snd

let rec private keys =
    function
    | JsonValue.Object fields -> fields |> List.collect (fun (key, value) -> key :: keys value)
    | JsonValue.Array fields -> fields |> List.collect keys
    | _ -> []

let private replaceBody (root: OperationRoot) bytes =
    let original = root.Structure.Compartments |> List.exactlyOne

    let compartment =
        Compartment.seal
            root.Header.InstallationId
            root.Header.OperationId
            original.Descriptor
            (nonce ())
            bytes
        |> valid

    OperationRoot.append root.Header root.Structure.Command root.Structure.Effects [ compartment ]
    |> valid

let private clear (root: OperationRoot) =
    OperationRoot.restore
        root.Header
        (OperationRoot.commandBytes root)
        (OperationRoot.effectBytes root)
        (root.Structure.Compartments |> List.map Compartment.withoutContent)
        root.Hash
    |> valid

[<Tests>]
let tests =
    testList
        "shared retained operation values"
        [
            testCase
                "live case results retain typed values without an encode/decode receipt loop"
                (fun () ->
                    let command, decision = decide 1 (CaseAct.Open Inputs.registration) None
                    let value = caseOperation 1 command decision

                    match value.Values with
                    | RetainedOperationValues.Case(Some actual) ->
                        Expect.isTrue
                            (obj.ReferenceEquals(actual.Command, command))
                            "Live receipt keeps the decided command object"

                        Expect.isTrue
                            (obj.ReferenceEquals(actual.Decision, decision))
                            "Live receipt keeps the decided result object"
                    | _ -> failtest "Available case values required"

                    match (RetainedOperation.decode value.Root |> valid).Values with
                    | RetainedOperationValues.Case(Some actual) ->
                        Expect.equal
                            actual.Command.Act
                            command.Act
                            "Stored business input is interpreted once into typed values"

                        Expect.equal
                            actual.Decision
                            decision
                            "Scoped private fields reconstruct the exact accepted state"
                    | _ -> failtest "Decoded case required"

                    let names =
                        body value.Root
                        |> StrictJson.read Compartment.MaximumContentBytes
                        |> valid
                        |> keys
                        |> Set.ofList

                    for metadata in
                        [
                            "operationId"
                            "caseId"
                            "caseReference"
                            "revision"
                            "expectedRevision"
                            "paymentId"
                        ] do
                        Expect.isFalse
                            (Set.contains metadata names)
                            "Private case values never duplicate the full structural request")
            testCase
                "payments carry one changed entry and derive its ID from public structure"
                (fun () ->
                    let _, opened = decide 1 (CaseAct.Open Inputs.registration) None
                    let _, decided = decide 2 (CaseAct.Decide Inputs.decision) (Some opened.After)

                    let command, paid =
                        decide
                            3
                            (CaseAct.RecordPayment(
                                Inputs.paymentWith
                                    [ BusinessField.PaidAmount, "3.0000" ]
                                    Inputs.payment
                            ))
                            (Some decided.After)

                    let value = caseOperation 3 command paid
                    let decoded = RetainedOperation.decode value.Root |> valid

                    match decoded.Values with
                    | RetainedOperationValues.Case(Some actual) ->
                        Expect.equal
                            actual.Decision
                            paid
                            "Summary and exact changed entry survive decoding"

                        match actual.Decision.Payment with
                        | Some(PaymentDifference.Recorded entry) ->
                            Expect.equal
                                (PaymentEntry.id entry)
                                (operation 3)
                                "Accepting ID comes from the root"
                        | _ -> failtest "Recorded effect required"
                    | _ -> failtest "Available case values required"

                    let text = body value.Root |> Encoding.UTF8.GetString

                    Expect.isFalse
                        (text.Contains("paymentId", StringComparison.Ordinal))
                        "Changed entry has no competing private ID"

                    Expect.isFalse
                        (text.Contains("payments", StringComparison.Ordinal))
                        "No full payment collection is retained"

                    match (RetainedOperation.decode (clear value.Root) |> valid).Values with
                    | RetainedOperationValues.Case None -> ()
                    | _ -> failtest "Cleared case facts must be explicitly unavailable"

                    Expect.equal
                        (clear value.Root).Structure.Effects
                        value.Root.Structure.Effects
                        "Payment reference remains public structural data")
            testCase
                "semantic finance and private canonical byte admission are separate controls"
                (fun () ->
                    let root = (opened ()).Root
                    let bytes = body root
                    let node = JsonNode.Parse bytes |> nonNull

                    let finance =
                        node["after"]
                        |> nonNull
                        |> fun after ->
                            after["fields"]
                            |> nonNull
                            |> fun fields -> fields["finance"] |> nonNull

                    finance["paidAmount"] <- System.Text.Json.Nodes.JsonValue.Create "1"

                    let changed =
                        node.ToJsonString()
                        |> Encoding.UTF8.GetBytes
                        |> StrictJson.read Compartment.MaximumContentBytes
                        |> valid
                        |> CanonicalWriter.bytes

                    match RetainedOperation.decode (replaceBody root changed) with
                    | Error(OperationContentProblem.PrivateFormat _) -> ()
                    | other ->
                        failtestf
                            "Contradictory financial facts must be interpreted, not merely parsed: %A"
                            other

                    let spaced = Array.append (Encoding.UTF8.GetBytes " ") bytes

                    Expect.equal
                        (RetainedOperation.decode (replaceBody root spaced))
                        (Error OperationContentProblem.NonCanonicalPrivateContent)
                        "Private bytea must be exactly canonical, unlike JSONB public metadata")
            testCase
                "known public-only erasure and note removal do not mint private nonces"
                (fun () ->
                    let _, opened = decide 1 (CaseAct.Open Inputs.registration) None
                    let command, erased = decide 2 (CaseAct.Bare BareAct.Erase) (Some opened.After)

                    let scope =
                        CommandSkeleton.Case
                            {
                                CaseId = parent
                                Reference = erased.After.Reference
                                ExpectedRevision = command.ExpectedRevision
                                Act = CaseActKind.Erase
                                SelectedPayment = None
                            }

                    let impossibleNonce () =
                        failtest "Public-only operation requested a private nonce"

                    let value =
                        RetainedOperation.create
                            (header 2 false)
                            scope
                            (OperationValues.Case { Command = command; Decision = erased })
                            impossibleNonce
                        |> valid

                    match (RetainedOperation.decode value.Root |> valid).Values with
                    | RetainedOperationValues.Case(Some actual) ->
                        Expect.equal
                            actual.Decision
                            erased
                            "Erased metadata is a complete known result"
                    | _ -> failtest "ERASE needs no private placeholder"

                    let note = NoteId.create (guid 7) |> Option.get
                    let input = NoteCommand.RemoveNote(parent, note)
                    let after = Note.ofAccepted note parent None

                    let removed =
                        RetainedOperation.create
                            (header 3 false)
                            (CommandSkeleton.Note(NoteCommandSkeleton.Remove(parent, note)))
                            (OperationValues.Note { Command = input; After = after })
                            impossibleNonce
                        |> valid

                    match (RetainedOperation.decode removed.Root |> valid).Values with
                    | RetainedOperationValues.Note(Some actual) ->
                        Expect.equal actual.Command input "Removal intent remains known"
                    | _ -> failtest "Removed note has no private contents")
            testCase
                "mixed people retains grants and an explicit unavailable profile after erasure"
                (fun () ->
                    let hdr = header 4 true
                    let target = ActorId.ofOperationId hdr.OperationId

                    let principal =
                        PrincipalKey.human "https://issuer.example/" "synthetic-target" |> valid

                    let profile =
                        ActorProfile.restore
                            {
                                DisplayName = "Saved\u202eName"
                                Contact = None
                            }
                        |> valid

                    Expect.isError
                        (ActorProfile.parse (ActorProfile.authored profile))
                        "Stored format may outlive current assertion policy"

                    let grants =
                        [
                            {
                                Role = Role.CaseHandler
                                Scope = Scope.Case parent
                            }
                            {
                                Role = Role.Auditor
                                Scope = Scope.Installation
                            }
                        ]

                    let command =
                        CommandSkeleton.People
                            {
                                TargetActorId = target
                                Principal = principal
                                Act = PeopleAct.Admit { Profile = 0; Grants = grants }
                            }

                    let value =
                        RetainedOperation.create
                            hdr
                            command
                            (OperationValues.People { Profile = Some profile })
                            nonce
                        |> valid

                    match (RetainedOperation.decode value.Root |> valid).Values with
                    | RetainedOperationValues.People(RetainedProfile.Available actual) ->
                        Expect.equal
                            (ActorProfile.authored actual)
                            (ActorProfile.authored profile)
                            "Permanent profile format does not re-admit current assertions"
                    | _ -> failtest "Available profile required"

                    let erasedRoot = clear value.Root

                    match (RetainedOperation.decode erasedRoot |> valid).Values with
                    | RetainedOperationValues.People RetainedProfile.Unavailable -> ()
                    | _ ->
                        failtest "Profile unavailability is explicit; no dummy profile is invented"

                    Expect.equal
                        erasedRoot.Structure.Effects[2..]
                        value.Root.Structure.Effects[2..]
                        "Grant values remain available for semantic replay"

                    let publicCommand =
                        CommandSkeleton.People
                            {
                                TargetActorId = target
                                Principal = principal
                                Act = PeopleAct.EraseProfile
                            }

                    let noProfile =
                        RetainedOperation.create
                            (header 5 true)
                            publicCommand
                            (OperationValues.People { Profile = None })
                            (fun () -> failtest "Public people act requested a nonce")
                        |> valid

                    Expect.equal
                        (RetainedOperation.decode noProfile.Root |> valid).Values
                        (RetainedOperationValues.People RetainedProfile.NotRequired)
                        "No profile requirement differs from an erased required profile")
            testCase "work and note values reconstruct identities from their root" (fun () ->
                let hdr = header 6 false

                let update =
                    CaseWorkInput.parse
                        {
                            Responsible =
                                WorkValue.Set(
                                    ActorId.format (ActorId.create (guid 7) |> Option.get)
                                )
                            Summary = WorkValue.Set "Saved handling summary"
                        }
                    |> valid

                let after =
                    {
                        CaseWorkSnapshot.CaseId = parent
                        Revision = Revision.first
                        Work =
                            {
                                Responsible = Some(ActorId.create (guid 7) |> Option.get)
                                Summary =
                                    Some(HandlingSummary.parse "Saved handling summary" |> valid)
                            }
                    }

                let value =
                    RetainedOperation.create
                        hdr
                        (CommandSkeleton.Work(parent, Revision.zero))
                        (OperationValues.Work { Update = update; After = after })
                        nonce
                    |> valid

                match (RetainedOperation.decode value.Root |> valid).Values with
                | RetainedOperationValues.Work(Some actual) ->
                    Expect.equal actual.After after "Work retains values and independent revision"
                | _ -> failtest "Work values required"

                let text = NoteText.parse "A note\nwith a second paragraph." |> valid
                let hdr = header 7 false

                let after =
                    Note.ofAccepted (NoteId.ofOperationId hdr.OperationId) parent (Some text)

                let value =
                    RetainedOperation.create
                        hdr
                        (CommandSkeleton.Note(NoteCommandSkeleton.Add parent))
                        (OperationValues.Note
                            {
                                Command = NoteCommand.AddNote(parent, text)
                                After = after
                            })
                        nonce
                    |> valid

                match (RetainedOperation.decode value.Root |> valid).Values with
                | RetainedOperationValues.Note(Some actual) ->
                    Expect.equal actual.After after "One text value serves input and result"
                | _ -> failtest "Note values required")
            testCase
                "a compound reminder retains both typed results under one case compartment"
                (fun () ->
                    let hdr = header 8 false
                    let oldId = ReminderId.create (guid 7) |> Option.get
                    let action text = ReminderAction.parse text |> valid
                    let date text = CalendarDate.parse text |> valid

                    let next =
                        {
                            ReminderPlan.Action = action "Next action"
                            Due = date "2026-12-01"
                        }

                    let input =
                        ReminderCommand.Change(
                            parent,
                            oldId,
                            Revision.first,
                            ReminderChange.CompleteAndPlanNext next
                        )

                    let old =
                        Reminder.ofAccepted
                            oldId
                            parent
                            (Revision.create 2L |> Option.get)
                            (action "Previous action")
                            (date "2026-11-01")
                            ReminderState.Completed
                        |> valid

                    let planned =
                        Reminder.ofAccepted
                            (ReminderId.ofOperationId hdr.OperationId)
                            parent
                            Revision.first
                            next.Action
                            next.Due
                            ReminderState.Pending
                        |> valid

                    let effects = ReminderEffects.CompletedAndPlanned(old, planned)

                    let scope =
                        CommandSkeleton.Reminder
                            {
                                CaseId = parent
                                Act = ReminderActKind.CompleteAndPlanNext
                                ReminderId = Some oldId
                                ExpectedRevision = Some Revision.first
                            }

                    let value =
                        RetainedOperation.create
                            hdr
                            scope
                            (OperationValues.Reminder { Command = input; Effects = effects })
                            nonce
                        |> valid

                    match (RetainedOperation.decode value.Root |> valid).Values with
                    | RetainedOperationValues.Reminder(Some actual) ->
                        Expect.equal
                            actual.Effects
                            effects
                            "Both accepted targets and revisions derive from their ordered descriptors"
                    | _ -> failtest "Compound reminder values required"

                    Expect.equal
                        value.Root.Structure.Compartments.Length
                        1
                        "Both private results have one case owner"

                    Expect.equal
                        value.Root.Structure.Compartments.Head.Descriptor.Effects
                        [ 0; 1 ]
                        "Compartment commits both effect references")
            testCase
                "wrong typed results and invalid headers refuse before nonce generation"
                (fun () ->
                    let command, decision = decide 1 (CaseAct.Open Inputs.registration) None

                    let scope =
                        CommandSkeleton.Case
                            {
                                CaseId = parent
                                Reference = decision.After.Reference
                                ExpectedRevision = Revision.zero
                                Act = CaseActKind.Open
                                SelectedPayment = None
                            }

                    let nonce () =
                        failtest "Invalid operation values reached nonce generation"

                    let bad =
                        { decision with
                            After =
                                { decision.After with
                                    CaseId = CaseId.ofOperationId (operation 7)
                                }
                        }

                    Expect.equal
                        (RetainedOperation.create
                            (header 1 false)
                            scope
                            (OperationValues.Case { Command = command; Decision = bad })
                            nonce)
                        (Error OperationContentProblem.Values)
                        "A result cannot be reparented"

                    Expect.equal
                        (RetainedOperation.create
                            { (header 1 false) with
                                BusinessDate = None
                            }
                            scope
                            (OperationValues.Case
                                {
                                    Command = command
                                    Decision = decision
                                })
                            nonce)
                        (Error(OperationContentProblem.Structure OperationStructureProblem.Header))
                        "Header prerequisites precede private encoding")
        ]

[<Tests>]
let caseAndReminderMatrix =
    testCase "case and reminder private codecs follow every declared operation kind" (fun () ->
        let _, opened = decide 1 (CaseAct.Open Inputs.registration) None
        let _, decided = decide 2 (CaseAct.Decide Inputs.decision) (Some opened.After)
        let _, paid = decide 3 (CaseAct.RecordPayment Inputs.payment) (Some decided.After)
        let _, closed = decide 4 (CaseAct.Bare BareAct.Close) (Some paid.After)
        let _, voided = decide 5 (CaseAct.Void "Entry correction") (Some paid.After)

        let samples =
            [
                None, CaseAct.Open Inputs.registration
                Some opened.After,
                CaseAct.AmendRegistration(
                    Inputs.registrationUpdate (
                        Inputs.registrationWith
                            [ BusinessField.ClaimantName, "Changed claimant" ]
                            Inputs.registration
                    )
                )
                Some opened.After, CaseAct.Decide Inputs.decision
                Some paid.After,
                CaseAct.Correct
                    {
                        Registration = Amendment.Keep
                        Decision =
                            Change.Update(
                                Inputs.decisionUpdate (
                                    Inputs.decisionWith
                                        [ BusinessField.PayableAmount, "1000" ]
                                        Inputs.decision
                                )
                            )
                    }
                Some decided.After, CaseAct.Bare BareAct.WithdrawDecision
                Some decided.After, CaseAct.RecordPayment Inputs.payment
                Some paid.After, CaseAct.RemovePayment(guid 3)
                Some paid.After, CaseAct.Bare BareAct.Close
                Some closed.After, CaseAct.Bare BareAct.Reopen
                Some paid.After, CaseAct.Void "Entry correction"
                Some voided.After, CaseAct.Reinstate "Confirmed reference"
                Some voided.After, CaseAct.Bare BareAct.Erase
            ]

        let seen = Collections.Generic.HashSet<CaseActKind>()

        for before, act in samples do
            let command =
                Case.parse
                    {
                        OperationId = guid 30
                        CaseReference = "PRIVATE-1"
                        ExpectedRevision =
                            before
                            |> Option.map (_.Revision >> Revision.value)
                            |> Option.defaultValue 0L
                        Act = act
                    }
                |> valid

            let context =
                match act, before with
                | CaseAct.RecordPayment _, Some _ -> Map.empty
                | CaseAct.RemovePayment _, Some before ->
                    let entry =
                        match paid.Payment with
                        | Some(PaymentDifference.Recorded entry) -> entry
                        | _ -> failtest "Known selected payment required"

                    (ClaimCore.TestSupport.CaseFixtures.paymentRows
                        (before.CaseId)
                        (Option.toList (Some entry)))
                | _ -> Map.empty

            let decision = Case.decideSnapshot today context command before |> valid
            let value = caseOperation 30 command decision

            match (RetainedOperation.decode value.Root |> valid).Values with
            | RetainedOperationValues.Case(Some decoded) ->
                Expect.equal
                    decoded.Command.Act
                    command.Act
                    "Private input retains typed case meaning"

                Expect.equal
                    decoded.Decision
                    decision
                    "Private result retains the exact accepted decision"
            | _ -> failtest "Available case values required"

            seen.Add(CaseAct.kind command.Act) |> ignore

        Expect.equal
            (seen |> Set.ofSeq)
            (CaseActKind.all |> Set.ofList)
            "Every declared case kind is exercised"

        let oldId = ReminderId.create (guid 7) |> Option.get
        let action = ReminderAction.parse "Original action" |> valid
        let due = CalendarDate.parse "2026-11-01" |> valid

        let plan =
            {
                ReminderPlan.Action = ReminderAction.parse "Next action" |> valid
                Due = CalendarDate.parse "2026-12-01" |> valid
            }

        let pending =
            Reminder.ofAccepted oldId parent Revision.first action due ReminderState.Pending
            |> valid

        let completed =
            Reminder.ofAccepted oldId parent Revision.first action due ReminderState.Completed
            |> valid

        for act in ReminderActKind.all do
            let input, before =
                match act with
                | ReminderActKind.Create -> ReminderCommand.Create(parent, plan), None
                | ReminderActKind.Update ->
                    ReminderCommand.Change(
                        parent,
                        oldId,
                        Revision.first,
                        ReminderChange.Update plan
                    ),
                    Some pending
                | ReminderActKind.Complete ->
                    ReminderCommand.Change(parent, oldId, Revision.first, ReminderChange.Complete),
                    Some pending
                | ReminderActKind.Cancel ->
                    ReminderCommand.Change(parent, oldId, Revision.first, ReminderChange.Cancel),
                    Some pending
                | ReminderActKind.Reopen ->
                    ReminderCommand.Change(
                        parent,
                        oldId,
                        Revision.first,
                        ReminderChange.Reopen plan.Due
                    ),
                    Some completed
                | ReminderActKind.CompleteAndPlanNext ->
                    ReminderCommand.Change(
                        parent,
                        oldId,
                        Revision.first,
                        ReminderChange.CompleteAndPlanNext plan
                    ),
                    Some pending

            let number = 40 + (ReminderActKind.all |> List.findIndex ((=) act))
            let hdr = header number false

            let effects =
                Reminder.decide hdr.OperationId opened.After before false input |> valid

            let target = if act = ReminderActKind.Create then None else Some oldId

            let expected =
                if act = ReminderActKind.Create then
                    None
                else
                    Some Revision.first

            let scope =
                CommandSkeleton.Reminder
                    {
                        CaseId = parent
                        Act = act
                        ReminderId = target
                        ExpectedRevision = expected
                    }

            let value =
                RetainedOperation.create
                    hdr
                    scope
                    (OperationValues.Reminder { Command = input; Effects = effects })
                    nonce
                |> valid

            match (RetainedOperation.decode value.Root |> valid).Values with
            | RetainedOperationValues.Reminder(Some decoded) ->
                Expect.equal decoded.Command input "Private reminder input remains typed"

                Expect.equal
                    decoded.Effects
                    effects
                    "All independently versioned result targets survive"
            | _ -> failtest "Available reminder values required")
