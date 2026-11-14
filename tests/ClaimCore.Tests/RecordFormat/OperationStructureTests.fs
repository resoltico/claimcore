module ClaimCore.Tests.OperationStructureTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat

let private valid result =
    Result.defaultWith (fun error -> failtestf "Structural operation fixture: %A" error) result

let private guid number =
    Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)

let private operation = OperationId.create (guid 2) |> Option.get

let private actor number =
    ActorId.create (guid number) |> Option.get

let private parent number =
    CaseId.create (guid number) |> Option.get

let private revision number = Revision.create number |> Option.get

let private principal =
    PrincipalKey.human "https://issuer.example/" "synthetic-owner" |> valid

let private header =
    {
        OperationHeader.InstallationId = Guid.Parse "00000000-0000-4000-8000-000000000001"
        Sequence = Sequence.create 42L |> Option.get
        PreviousHash = EntryHash.zero
        OperationId = operation
        ActorId = actor 9
        RecordedAt = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)
        BusinessDate = Some(DateOnly(2026, 9, 7))
    }

let private reference = CaseReference.parse "STRUCTURE-1" |> valid

let private content header ordinal owner purpose refs =
    let descriptor = CompartmentDescriptor.create ordinal owner purpose refs |> valid

    Compartment.seal
        header.InstallationId
        header.OperationId
        descriptor
        [| 0uy .. 31uy |]
        (Encoding.UTF8.GetBytes """{"private":"synthetic"}""")
    |> valid

let private caseCommand act selection =
    CommandSkeleton.Case
        {
            CaseId = parent 3
            Reference = reference
            ExpectedRevision = revision 4L
            Act = act
            SelectedPayment = selection
        }

let private caseEffect act payment =
    EffectDescriptor.Case(parent 3, revision 5L, act, None, payment)

let private caseContent () =
    content header 0 (CompartmentOwner.Case(parent 3)) CompartmentPurpose.Case [ 0 ]

[<Tests>]
let tests =
    testList
        "operation structural closure"
        [
            testCase "erased payment command and effect retain the selected identity" (fun () ->
                let selected = OperationId.create (guid 7) |> Option.get
                let command = caseCommand CaseActKind.RemovePayment (Some selected)

                let effects =
                    [
                        caseEffect
                            CaseActKind.RemovePayment
                            (Some(PaymentReference.Removed selected))
                    ]

                let available = caseContent ()

                let complete =
                    OperationStructure.append header command effects [ available ] |> valid

                Expect.equal
                    complete.Effects
                    effects
                    "Public effect retains selection without private date or amount"

                let erased = Compartment.withoutContent available

                let restored =
                    OperationStructure.restore header command effects [ erased ] |> valid

                Expect.equal
                    restored.Command
                    command
                    "Selective erasure cannot remove command identity"

                Expect.equal
                    restored.Effects
                    effects
                    "Removal markers remain independently reconstructible"

                Expect.equal
                    (OperationStructure.append header command effects [ erased ])
                    (Error OperationStructureProblem.UnavailableAppendContent)
                    "A newly appended operation cannot claim pre-erased contents")
            testCase
                "a case erasure has no private compartment and a payment recording binds its accepting ID"
                (fun () ->
                    Expect.isOk
                        (OperationStructure.append
                            header
                            (caseCommand CaseActKind.Erase None)
                            [ caseEffect CaseActKind.Erase None ]
                            [])
                        "Case erasure is retained public structure"

                    let recorded =
                        [
                            caseEffect
                                CaseActKind.RecordPayment
                                (Some(PaymentReference.Recorded operation))
                        ]

                    Expect.isOk
                        (OperationStructure.append
                            header
                            (caseCommand CaseActKind.RecordPayment None)
                            recorded
                            [ caseContent () ])
                        "Payment accepting identity belongs to the root"

                    let wrong = OperationId.create (guid 7) |> Option.get

                    Expect.equal
                        (OperationStructure.append
                            header
                            (caseCommand CaseActKind.RecordPayment None)
                            [
                                caseEffect
                                    CaseActKind.RecordPayment
                                    (Some(PaymentReference.Recorded wrong))
                            ]
                            [ caseContent () ])
                        (Error OperationStructureProblem.Effects)
                        "Another operation cannot donate a payment identity")
            testCase
                "mixed ADMIT retains grants when its independently owned profile is unavailable"
                (fun () ->
                    let target = ActorId.ofOperationId operation
                    let peopleHeader = { header with BusinessDate = None }

                    let grants =
                        [
                            {
                                Role = Role.CaseHandler
                                Scope = Scope.Case(parent 3)
                            }
                            {
                                Role = Role.PaymentRecorder
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

                    let effects =
                        [
                            EffectDescriptor.People(
                                target,
                                PeopleEffectDescriptor.Register principal
                            )
                            EffectDescriptor.People(target, PeopleEffectDescriptor.SetProfile 0)
                            EffectDescriptor.People(target, PeopleEffectDescriptor.Grant grants[0])
                            EffectDescriptor.People(target, PeopleEffectDescriptor.Grant grants[1])
                        ]

                    let profile =
                        content
                            peopleHeader
                            0
                            (CompartmentOwner.ActorProfile target)
                            CompartmentPurpose.Profile
                            [ 1 ]

                    Expect.isOk
                        (OperationStructure.append peopleHeader command effects [ profile ])
                        "Complete mixed command closes before append"

                    let restored =
                        OperationStructure.restore
                            peopleHeader
                            command
                            effects
                            [ Compartment.withoutContent profile ]
                        |> valid

                    Expect.equal
                        restored.Effects[2..]
                        effects[2..]
                        "Erasure never discards or fabricates public grants"

                    for forged in
                        [ effects |> List.rev; List.tail effects; effects @ [ effects[1] ] ] do
                        Expect.equal
                            (OperationStructure.restore peopleHeader command forged [ profile ])
                            (Error OperationStructureProblem.Effects)
                            "Missing, extra or reordered effects cannot close"

                    let changedGrant =
                        EffectDescriptor.People(
                            target,
                            PeopleEffectDescriptor.Grant
                                { grants[0] with
                                    Scope = Scope.Case(parent 4)
                                }
                        )

                    Expect.equal
                        (OperationStructure.restore
                            peopleHeader
                            command
                            [ effects[0]; effects[1]; changedGrant; effects[3] ]
                            [ profile ])
                        (Error OperationStructureProblem.Effects)
                        "Reassigned grant references cannot close")
            testCase
                "missing extra reordered and rebound compartment descriptors are rejected"
                (fun () ->
                    let command = caseCommand CaseActKind.Decide None
                    let effects = [ caseEffect CaseActKind.Decide None ]
                    let original = caseContent ()

                    for contents in
                        [
                            []
                            [ original; original ]
                            [
                                content
                                    header
                                    1
                                    (CompartmentOwner.Case(parent 3))
                                    CompartmentPurpose.Case
                                    [ 0 ]
                            ]
                            [
                                content
                                    header
                                    0
                                    (CompartmentOwner.Case(parent 4))
                                    CompartmentPurpose.Case
                                    [ 0 ]
                            ]
                            [
                                content
                                    header
                                    0
                                    (CompartmentOwner.Case(parent 3))
                                    CompartmentPurpose.Work
                                    [ 0 ]
                            ]
                            [
                                content
                                    header
                                    0
                                    (CompartmentOwner.Case(parent 3))
                                    CompartmentPurpose.Case
                                    [ 1 ]
                            ]
                        ] do
                        Expect.equal
                            (OperationStructure.restore header command effects contents)
                            (Error OperationStructureProblem.Compartments)
                            "Only the declared exact owner, purpose, ordinal and references belong to the command"

                    let otherHeader =
                        { header with
                            OperationId = OperationId.create (guid 8) |> Option.get
                        }

                    let other =
                        content
                            otherHeader
                            0
                            (CompartmentOwner.Case(parent 3))
                            CompartmentPurpose.Case
                            [ 0 ]

                    Expect.equal
                        (OperationStructure.restore header command effects [ other ])
                        (Error OperationStructureProblem.CompartmentBinding)
                        "A structurally matching compartment remains bound to its operation")
            testCase
                "compound reminder effects have distinct IDs and positions inside one sequence"
                (fun () ->
                    let old = ReminderId.create (guid 7) |> Option.get
                    let next = ReminderId.ofOperationId operation

                    let command =
                        CommandSkeleton.Reminder
                            {
                                CaseId = parent 3
                                Act = ReminderActKind.CompleteAndPlanNext
                                ReminderId = Some old
                                ExpectedRevision = Some(revision 2L)
                            }

                    let effects =
                        [
                            EffectDescriptor.ReminderChanged(
                                parent 3,
                                old,
                                revision 3L,
                                ReminderActKind.Complete
                            )
                            EffectDescriptor.ReminderCreated(parent 3, next, Revision.first)
                        ]

                    let payload =
                        content
                            header
                            0
                            (CompartmentOwner.Case(parent 3))
                            CompartmentPurpose.Reminder
                            [ 0; 1 ]

                    let value =
                        OperationStructure.append header command effects [ payload ] |> valid

                    Expect.equal
                        value.Header.Sequence
                        header.Sequence
                        "One operation advances one sequence"

                    let first = EffectPosition.create value.Header.Sequence 0 |> Option.get
                    let second = EffectPosition.create value.Header.Sequence 1 |> Option.get
                    Expect.isLessThan first second "The second effect is ordered independently"

                    Expect.notEqual
                        (EffectId.create operation 0)
                        (EffectId.create operation 1)
                        "Sequence alone is not an effect identity"

                    Expect.equal
                        (OperationStructure.append header command (List.rev effects) [ payload ])
                        (Error OperationStructureProblem.Effects)
                        "Effect order is immutable"

                    let incomplete =
                        content
                            header
                            0
                            (CompartmentOwner.Case(parent 3))
                            CompartmentPurpose.Reminder
                            [ 0 ]

                    Expect.equal
                        (OperationStructure.append header command effects [ incomplete ])
                        (Error OperationStructureProblem.Compartments)
                        "The private plan is bound to both changed reminders")
            testCase "operation headers separate business dates from recording instants" (fun () ->
                let command = caseCommand CaseActKind.Close None
                let effects = [ caseEffect CaseActKind.Close None ]

                for invalid in
                    [
                        { header with
                            InstallationId = Guid.Empty
                        }
                        { header with Sequence = Sequence.zero }
                        { header with BusinessDate = None }
                        { header with
                            RecordedAt = header.RecordedAt.AddTicks 1L
                        }
                    ] do
                    Expect.equal
                        (OperationStructure.restore invalid command effects [ caseContent () ])
                        (Error OperationStructureProblem.Header)
                        "Stored header has one valid installation, positive sequence, business date and microsecond instant"

                let offset =
                    { header with
                        RecordedAt = header.RecordedAt.ToOffset(TimeSpan.FromHours 3.)
                    }

                let normalized =
                    OperationStructure.restore offset command effects [ caseContent () ] |> valid

                Expect.equal
                    normalized.Header.RecordedAt.Offset
                    TimeSpan.Zero
                    "Recorded time normalizes the same instant to UTC"

                Expect.equal
                    normalized.Header.RecordedAt
                    header.RecordedAt
                    "Normalization never changes the instant")
            testCase
                "case creation and bounded command shapes enforce their own identities"
                (fun () ->
                    let id = CaseId.ofOperationId operation

                    let creation =
                        CommandSkeleton.Case
                            {
                                CaseId = id
                                Reference = reference
                                ExpectedRevision = Revision.zero
                                Act = CaseActKind.Open
                                SelectedPayment = None
                            }

                    let effects =
                        [
                            EffectDescriptor.Case(
                                id,
                                Revision.first,
                                CaseActKind.Open,
                                Some reference,
                                None
                            )
                        ]

                    let values =
                        content header 0 (CompartmentOwner.Case id) CompartmentPurpose.Case [ 0 ]

                    Expect.isOk
                        (OperationStructure.append header creation effects [ values ])
                        "OPEN creates its operation-derived case identity"

                    for invalid in
                        [
                            caseCommand CaseActKind.Open None
                            caseCommand CaseActKind.RemovePayment None
                            caseCommand CaseActKind.Close (Some operation)
                        ] do
                        Expect.equal
                            (OperationStructure.restore header invalid effects [ values ])
                            (Error OperationStructureProblem.Command)
                            "Public command cannot encode incompatible identity or selection"

                    let terminal = CommandSkeleton.Work(parent 3, revision Revision.Maximum)

                    Expect.equal
                        (OperationStructure.restore header terminal [] [])
                        (Error OperationStructureProblem.Command)
                        "A bounded revision cannot overflow on acceptance")
            testCase
                "ADMIT and owner provisioning keep their declared task bounds and ordering"
                (fun () ->
                    let target = ActorId.ofOperationId operation
                    let peopleHeader = { header with BusinessDate = None }

                    let profile =
                        content
                            peopleHeader
                            0
                            (CompartmentOwner.ActorProfile target)
                            CompartmentPurpose.Profile
                            [ 1 ]

                    let grants =
                        [
                            for number in 10..57 ->
                                {
                                    Role = Role.CaseHandler
                                    Scope = Scope.Case(parent number)
                                }
                        ]

                    let command grants =
                        CommandSkeleton.People
                            {
                                TargetActorId = target
                                Principal = principal
                                Act = PeopleAct.Admit { Profile = 0; Grants = grants }
                            }

                    let effects =
                        [
                            yield
                                EffectDescriptor.People(
                                    target,
                                    PeopleEffectDescriptor.Register principal
                                )
                            yield
                                EffectDescriptor.People(
                                    target,
                                    PeopleEffectDescriptor.SetProfile 0
                                )
                            for grant in grants ->
                                EffectDescriptor.People(target, PeopleEffectDescriptor.Grant grant)
                        ]

                    let maximum =
                        OperationStructure.append peopleHeader (command grants) effects [ profile ]
                        |> valid

                    Expect.equal
                        maximum.Effects.Length
                        50
                        "Forty-eight grants plus registration/profile is one bounded operation"

                    for invalid in
                        [
                            command []
                            command (grants @ [ grants.Head ])
                            command (
                                grants
                                @ [
                                    {
                                        Role = Role.CaseHandler
                                        Scope = Scope.Case(parent 58)
                                    }
                                ]
                            )
                            CommandSkeleton.People
                                {
                                    TargetActorId = target
                                    Principal = principal
                                    Act = PeopleAct.Register 1
                                }
                        ] do
                        Expect.equal
                            (OperationStructure.restore peopleHeader invalid effects [ profile ])
                            (Error OperationStructureProblem.Command)
                            "Missing grants, duplicate grants, overflow and unknown profile ordinal are not accepted command shapes"

                    let ownerHeader =
                        { peopleHeader with
                            Sequence = Sequence.first
                            ActorId = target
                        }

                    let roles = [ Role.CaseHandler; Role.Auditor ]

                    let owner =
                        CommandSkeleton.People
                            {
                                TargetActorId = target
                                Principal = principal
                                Act = PeopleAct.ProvisionOwner(0, roles)
                            }

                    let ownerEffects =
                        [
                            EffectDescriptor.People(
                                target,
                                PeopleEffectDescriptor.ProvisionOwner principal
                            )
                            EffectDescriptor.People(target, PeopleEffectDescriptor.SetProfile 0)
                            EffectDescriptor.People(
                                target,
                                PeopleEffectDescriptor.Grant
                                    {
                                        Role = Role.CaseHandler
                                        Scope = Scope.Installation
                                    }
                            )
                            EffectDescriptor.People(
                                target,
                                PeopleEffectDescriptor.Grant
                                    {
                                        Role = Role.Auditor
                                        Scope = Scope.Installation
                                    }
                            )
                        ]

                    let ownerProfile =
                        content
                            ownerHeader
                            0
                            (CompartmentOwner.ActorProfile target)
                            CompartmentPurpose.Profile
                            [ 1 ]

                    Expect.isOk
                        (OperationStructure.append ownerHeader owner ownerEffects [ ownerProfile ])
                        "Owner, profile and initial roles close in the first atomic operation"

                    Expect.equal
                        (OperationStructure.restore peopleHeader owner ownerEffects [ profile ])
                        (Error OperationStructureProblem.Command)
                        "Owner provisioning cannot occur later or under a different executor")
        ]
