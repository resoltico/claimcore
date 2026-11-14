module ClaimCore.Tests.OperationRootTests

open System
open System.Text
open System.Security.Cryptography
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat

let private valid result =
    Result.defaultWith (fun error -> failtestf "Root vector: %A" error) result

let private guid number =
    Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)

let private operation = OperationId.create (guid 2) |> Option.get
let private parent = CaseId.create (guid 3) |> Option.get
let private note = NoteId.create (guid 7) |> Option.get
let private selected = OperationId.create (guid 7) |> Option.get
let private utf8 (text: string) = Encoding.UTF8.GetBytes text

let private header =
    {
        OperationHeader.InstallationId = Guid.Parse "00000000-0000-4000-8000-000000000001"
        Sequence = Sequence.create 42L |> Option.get
        PreviousHash = EntryHash.zero
        OperationId = operation
        ActorId = ActorId.create (guid 9) |> Option.get
        RecordedAt = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)
        BusinessDate = Some(DateOnly(2026, 9, 7))
    }

let private commandText =
    """{"act":"note.remove","caseId":"00000000-0000-7000-8000-000000000003","compartment":null,"kind":"NOTE","noteId":"00000000-0000-7000-8000-000000000007"}"""

let private effectText =
    """{"caseId":"00000000-0000-7000-8000-000000000003","kind":"NOTE_REMOVED","noteId":"00000000-0000-7000-8000-000000000007"}"""

let private command = CommandSkeleton.Note(NoteCommandSkeleton.Remove(parent, note))
let private effect = EffectDescriptor.NoteRemoved(parent, note)

// Independent stdlib derivation: framed ASCII domain tag, format3, RFC4122 UUIDs,
// i64BE sequence, prior hash, operation/executor UUIDs, i64BE UTC ticks,
// optional business-date flag + u32BE zero-based Gregorian day, then framed canonical
// command, effect count, each ordinal/framed descriptor, compartment count/descriptors/hashes.
// UTC ticks = 739865 Gregorian days * 864000000000 ticks/day = 639243360000000000.
let private prefix =
    Convert.FromHexString(
        String.concat
            ""
            [
                "00000013"
                "436c61696d436f72652e4f7065726174696f6e"
                "00000003"
                "00000000000040008000000000000001"
                "000000000000002a"
                "0000000000000000000000000000000000000000000000000000000000000000"
                "00000000000070008000000000000002"
                "00000000000070008000000000000009"
                "08df0c72f5d3c000"
                "01"
                "000b4a19"
            ]
    )

let private digest =
    Convert.FromHexString "014470a76e9cb310bf4901960457c5d235543edc667f5e00d45b398c4a00cfb0"

let private expectedHash = EntryHash.ofBytes digest |> Option.get

[<Tests>]
let tests =
    testList
        "canonical operation roots"
        [
            testCase "an independently framed public operation derives the literal hash" (fun () ->
                let bytes =
                    Array.concat
                        [
                            prefix
                            Convert.FromHexString "00000096"
                            utf8 commandText
                            Convert.FromHexString "000000010000000000000077"
                            utf8 effectText
                            Convert.FromHexString "00000000"
                        ]

                Expect.equal bytes.Length 417 "Hand-counted root framing length"

                Expect.equal
                    (SHA256.HashData bytes)
                    digest
                    "Independent preimage derives the fixed vector"

                let root = OperationRoot.append header command [ effect ] [] |> valid

                Expect.equal
                    (OperationRoot.commandBytes root)
                    (utf8 commandText)
                    "Command codec matches independently authored canonical text"

                Expect.equal
                    (OperationRoot.effectBytes root)
                    [ 0, utf8 effectText ]
                    "Effect codec matches independently authored canonical text"

                Expect.equal
                    root.Hash
                    expectedHash
                    "Actual root encoding includes every independently framed field"

                Expect.equal
                    (OperationRoot.restore
                        header
                        (utf8 commandText)
                        [ 0, utf8 effectText ]
                        []
                        expectedHash)
                    (Ok root)
                    "Stored public metadata closes and verifies")
            testCase
                "a salted Unicode compartment is bound into the operation root and survives content erasure"
                (fun () ->
                    let command =
                        CommandSkeleton.Case
                            {
                                CaseId = parent
                                Reference = CaseReference.parse "STRUCTURE-1" |> valid
                                ExpectedRevision = Revision.create 4L |> Option.get
                                Act = CaseActKind.RemovePayment
                                SelectedPayment = Some selected
                            }

                    let effect =
                        EffectDescriptor.Case(
                            parent,
                            Revision.create 5L |> Option.get,
                            CaseActKind.RemovePayment,
                            None,
                            Some(PaymentReference.Removed selected)
                        )

                    let descriptor =
                        CompartmentDescriptor.create
                            0
                            (CompartmentOwner.Case parent)
                            CompartmentPurpose.Case
                            [ 0 ]
                        |> valid

                    let payload = utf8 """{"claimant":"Ā 😀"}"""

                    let compartment =
                        Compartment.seal
                            header.InstallationId
                            operation
                            descriptor
                            [| 0uy .. 31uy |]
                            payload
                        |> valid

                    let root =
                        OperationRoot.append header command [ effect ] [ compartment ] |> valid

                    let commandText =
                        """{"act":"case.removePayment","caseId":"00000000-0000-7000-8000-000000000003","compartment":0,"expectedRevision":4,"kind":"CASE","reference":"STRUCTURE-1","selectedPayment":"00000000-0000-7000-8000-000000000007"}"""

                    let effectText =
                        """{"act":"case.removePayment","caseId":"00000000-0000-7000-8000-000000000003","creationReference":null,"kind":"CASE","payment":{"kind":"REMOVED","operationId":"00000000-0000-7000-8000-000000000007"},"revision":5}"""

                    let descriptorBytes =
                        Convert.FromHexString
                            "010000000000007000800000000000000301000000000000000100000000"

                    let commitment =
                        Convert.FromHexString
                            "dd0bbf62770b0ac413b1ec2da4414ed06fd6147eb5703972aefe3ef7b9154d75"

                    let bytes =
                        Array.concat
                            [
                                prefix
                                Convert.FromHexString "000000d2"
                                utf8 commandText
                                Convert.FromHexString "0000000100000000000000d2"
                                utf8 effectText
                                Convert.FromHexString "000000010000001e"
                                descriptorBytes
                                commitment
                            ]

                    let hash =
                        Convert.FromHexString
                            "cecc8d84b86e43c050deed54a3d003a75a07dc344b36f623367cf2589f7871d1"

                    Expect.equal bytes.Length 634 "Hand-counted salted root framing"
                    Expect.equal (SHA256.HashData bytes) hash "Independent salted root derivation"

                    Expect.equal
                        (EntryHash.toBytes root.Hash)
                        hash
                        "Root binds ordered compartment descriptor and commitment"

                    let erased =
                        OperationRoot.restore
                            header
                            (utf8 commandText)
                            [ 0, utf8 effectText ]
                            [ Compartment.withoutContent compartment ]
                            root.Hash
                        |> valid

                    Expect.equal erased.Hash root.Hash "Erasure never rewrites an accepted root"

                    Expect.equal
                        erased.Structure.Effects
                        [ effect ]
                        "Selected payment identity remains after private content is gone"

                    let changed =
                        Compartment.seal
                            header.InstallationId
                            operation
                            descriptor
                            [| 1uy .. 32uy |]
                            payload
                        |> valid

                    Expect.equal
                        (OperationRoot.restore
                            header
                            (utf8 commandText)
                            [ 0, utf8 effectText ]
                            [ changed ]
                            root.Hash)
                        (Error OperationRootProblem.Hash)
                        "A substituted salt/content commitment changes the root")
            testCase "recording metadata and operation context cannot be reassigned" (fun () ->
                let root = OperationRoot.append header command [ effect ] [] |> valid

                let changedHeaders =
                    [
                        { header with
                            InstallationId = Guid.Parse "00000000-0000-4000-8000-000000000004"
                        }
                        { header with
                            Sequence = Sequence.create 43L |> Option.get
                        }
                        { header with
                            PreviousHash = EntryHash.ofBytes (Array.create 32 1uy) |> Option.get
                        }
                        { header with
                            OperationId = OperationId.create (guid 8) |> Option.get
                        }
                        { header with
                            ActorId = ActorId.create (guid 8) |> Option.get
                        }
                        { header with
                            RecordedAt = header.RecordedAt.AddTicks 10L
                        }
                        { header with
                            BusinessDate = Some(DateOnly(2026, 9, 8))
                        }
                    ]

                for changed in changedHeaders do
                    Expect.equal
                        (OperationRoot.restore
                            changed
                            (utf8 commandText)
                            [ 0, utf8 effectText ]
                            []
                            root.Hash)
                        (Error OperationRootProblem.Hash)
                        "Every root context field is committed")
            testCase "stored effect ordinals cannot disappear repeat or leave gaps" (fun () ->
                for effects in
                    [
                        []
                        [ 1, utf8 effectText ]
                        [ 0, utf8 effectText; 0, utf8 effectText ]
                        [ 0, utf8 effectText; 2, utf8 effectText ]
                    ] do
                    Expect.equal
                        (OperationRoot.restore header (utf8 commandText) effects [] expectedHash)
                        (Error OperationRootProblem.EffectOrdinals)
                        "Row ordinals are the actual complete ordered effect positions")
            testCase
                "public metadata admits JSONB spelling and rejects private or unknown fields"
                (fun () ->
                    let pretty =
                        utf8
                            """{ "kind":"NOTE", "noteId":"00000000-0000-7000-8000-000000000007", "caseId":"00000000-0000-7000-8000-000000000003", "act":"note.remove", "compartment":null }"""

                    Expect.isOk
                        (OperationRoot.restore header pretty [ 0, utf8 effectText ] [] expectedHash)
                        "Canonical hash depends on interpreted metadata, not JSONB formatting"

                    for invalid in
                        [
                            commandText.Replace("\"compartment\":null,", "")
                            commandText.Replace("\"kind\":\"NOTE\"", "\"kind\":\"UNKNOWN\"")
                            commandText.Replace("}", ",\"text\":\"private narrative\"}")
                        ] do
                        match
                            OperationRoot.restore
                                header
                                (utf8 invalid)
                                [ 0, utf8 effectText ]
                                []
                                expectedHash
                        with
                        | Error(OperationRootProblem.PublicMetadata _) -> ()
                        | other -> failtestf "Expected closed public metadata refusal: %A" other)
            testCase
                "bound effects expose operation-scoped proof and lexicographic order"
                (fun () ->
                    let target = ReminderId.create (guid 7) |> Option.get
                    let next = ReminderId.ofOperationId operation

                    let command =
                        CommandSkeleton.Reminder
                            {
                                CaseId = parent
                                Act = ReminderActKind.CompleteAndPlanNext
                                ReminderId = Some target
                                ExpectedRevision = Some(Revision.create 2L |> Option.get)
                            }

                    let effects =
                        [
                            EffectDescriptor.ReminderChanged(
                                parent,
                                target,
                                Revision.create 3L |> Option.get,
                                ReminderActKind.Complete
                            )
                            EffectDescriptor.ReminderCreated(parent, next, Revision.first)
                        ]

                    let descriptor =
                        CompartmentDescriptor.create
                            0
                            (CompartmentOwner.Case parent)
                            CompartmentPurpose.Reminder
                            [ 0; 1 ]
                        |> valid

                    let compartment =
                        Compartment.seal
                            header.InstallationId
                            operation
                            descriptor
                            [| 0uy .. 31uy |]
                            (utf8 """{"private":"synthetic"}""")
                        |> valid

                    let root = OperationRoot.append header command effects [ compartment ] |> valid
                    let bound = OperationRoot.effects root

                    Expect.equal
                        (bound |> List.map (fun effect -> EffectId.operation effect.Id))
                        [ operation; operation ]
                        "One operation owns both identities"

                    Expect.equal
                        (bound |> List.map (fun effect -> EffectId.index effect.Id))
                        [ 0; 1 ]
                        "Indices remain distinct"

                    Expect.isLessThan
                        bound[0].Position
                        bound[1].Position
                        "Ordering includes the effect ordinal"

                    Expect.equal
                        root.Position.Sequence
                        header.Sequence
                        "The root advances once for a compound command"

                    Expect.isOk
                        (OperationRoot.restore
                            header
                            (OperationRoot.commandBytes root)
                            (OperationRoot.effectBytes root)
                            [ compartment ]
                            root.Hash)
                        "Complete compound root closes and verifies")
        ]

[<Tests>]
let publicCodecMatrix =
    testCase "all declared command families retain their typed public metadata" (fun () ->
        let principal = PrincipalKey.human "https://issuer.example/" "م Ā 😀" |> valid
        let target = ActorId.ofOperationId operation
        let peopleHeader = { header with BusinessDate = None }

        let grant =
            {
                Role = Role.CaseHandler
                Scope = Scope.Case parent
            }

        let people act =
            CommandSkeleton.People
                {
                    TargetActorId = target
                    Principal = principal
                    Act = act
                }

        let ownerHeader =
            { peopleHeader with
                Sequence = Sequence.first
                ActorId = target
            }

        let commands =
            [
                for kind in CaseActKind.all do
                    let opening = kind = CaseActKind.Open

                    yield
                        header,
                        CommandSkeleton.Case
                            {
                                CaseId = if opening then CaseId.ofOperationId operation else parent
                                Reference = CaseReference.parse "STRUCTURE-1" |> valid
                                ExpectedRevision =
                                    if opening then Revision.zero else Revision.first
                                Act = kind
                                SelectedPayment =
                                    if kind = CaseActKind.RemovePayment then
                                        Some selected
                                    else
                                        None
                            }
                yield peopleHeader, people (PeopleAct.Register 0)
                yield peopleHeader, people (PeopleAct.Admit { Profile = 0; Grants = [ grant ] })
                yield peopleHeader, people (PeopleAct.SetProfile 0)
                yield peopleHeader, people PeopleAct.EraseProfile
                yield peopleHeader, people (PeopleAct.Grant grant)
                yield peopleHeader, people (PeopleAct.Revoke grant)
                yield peopleHeader, people PeopleAct.Enable
                yield peopleHeader, people PeopleAct.Disable
                yield ownerHeader, people (PeopleAct.ProvisionOwner(0, [ Role.Auditor ]))
                yield header, CommandSkeleton.Work(parent, Revision.zero)
                yield header, CommandSkeleton.Note(NoteCommandSkeleton.Add parent)
                yield header, command
                for act in ReminderActKind.all do
                    yield
                        header,
                        CommandSkeleton.Reminder
                            {
                                CaseId = parent
                                Act = act
                                ReminderId =
                                    if act = ReminderActKind.Create then
                                        None
                                    else
                                        Some note
                                        |> Option.map (
                                            NoteId.value >> ReminderId.create >> Option.get
                                        )
                                ExpectedRevision =
                                    if act = ReminderActKind.Create then
                                        None
                                    else
                                        Some Revision.first
                            }
            ]
        // Shared closure supplies coherent inputs only. The independent vectors and closure
        // mutation controls establish the required encoding and structural rules separately.
        for header, command in commands do
            let effects, descriptors = OperationStructure.expected header command |> valid

            let compartments =
                descriptors
                |> List.map (fun descriptor ->
                    Compartment.seal
                        header.InstallationId
                        header.OperationId
                        descriptor
                        [| 0uy .. 31uy |]
                        (utf8 """{"private":"synthetic"}""")
                    |> valid)

            let root = OperationRoot.append header command effects compartments |> valid

            let decoded =
                OperationRoot.restore
                    header
                    (OperationRoot.commandBytes root)
                    (OperationRoot.effectBytes root)
                    compartments
                    root.Hash
                |> valid

            Expect.equal
                decoded.Structure.Command
                command
                "Principal, grant scope, selected handles and expected revision survive the public codec"

            Expect.equal
                decoded.Structure.Effects
                effects
                "Ordered typed descriptors survive the public codec")
