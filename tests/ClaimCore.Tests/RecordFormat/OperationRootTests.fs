module ClaimCore.Tests.OperationRootTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Json
open ClaimCore.RecordFormat
open ClaimCore.Tests.RecordBuilders

let private restoreWith header task structure effects compartments hash =
    OperationRoot.restore header task structure effects compartments hash

let private read (bytes: byte array) =
    StrictJson.read Int32.MaxValue bytes |> required

let private setMember name value =
    function
    | JsonValue.Object members ->
        JsonValue.Object((name, value) :: (members |> List.filter (fst >> (<>) name)))
    | _ -> failtest "Expected object mutation witness."

[<Tests>]
let tests =
    testList
        "format 5 operation roots"
        [
            testCase
                "independently framed public metadata derives the literal format 5 digest"
                (fun () ->
                    let record = publicRecord ()
                    let root = record.Root
                    // Independent Python struct/uuid/hashlib calculation: 545 bytes, .NET UTC ticks below.
                    Expect.equal
                        root.Header.RecordedAt.UtcTicks
                        639297792000000000L
                        "Timestamp frame"

                    Expect.equal
                        (EntryHash.toBytes root.Hash |> Convert.ToHexStringLower)
                        "1eafd7d8cb9c025ef799e0e5d44c5c797d13fbdcf4c1cd8ae34f1c54ee0985ea"
                        "Literal SHA-256 vector"

                    Expect.equal
                        (OperationRoot.structureBytes root |> Encoding.UTF8.GetString)
                        """{"binding":{"actorId":"00000000-0000-7000-8000-000000000002","expectedRevision":1,"kind":"PEOPLE"},"retained":{"collections":{}}}"""
                        "Hand-authored canonical structure"

                    Expect.equal
                        (OperationRoot.effectBytes root
                         |> List.exactlyOne
                         |> snd
                         |> Encoding.UTF8.GetString)
                        """{"aggregate":{"id":"00000000-0000-7000-8000-000000000002","kind":"PERSON"},"erases":{"id":"00000000-0000-7000-8000-000000000002","kind":"PROFILE"},"part":{"id":"00000000-0000-7000-8000-000000000002","kind":"PERSON"},"retained":{"enabled":true,"grants":[]},"revision":2}"""
                        "Hand-authored canonical effect"

                    Expect.equal (restore root) (Ok root) "Public-only root restores")
            testCase
                "all six part kinds restore with available or erased compartment content"
                (fun () ->
                    let records = samples ()
                    let kinds = records |> List.collect _.Changes |> List.map _.Kind |> Set.ofList

                    Expect.equal
                        kinds
                        (set
                            [
                                PartKind.Case
                                PartKind.Payment
                                PartKind.Work
                                PartKind.Reminder
                                PartKind.Note
                                PartKind.Person
                            ])
                        "Independent part inventory"

                    for record in records do
                        Expect.equal
                            (restore record.Root)
                            (Ok record.Root)
                            (TaskId.token record.Root.Structure.Task)

                        Expect.equal
                            (RetainedOperation.restore record.Root)
                            (Ok record)
                            "Available accepted values round trip"

                        let erased = clear record

                        Expect.equal
                            erased.Root.Hash
                            record.Root.Hash
                            "Erasure cannot change the root"

                        Expect.isOk (restore erased.Root) "Erased root is still hash-verifiable")
            testCase "recording metadata, task and binding cannot be reassigned" (fun () ->
                let root = (publicRecord ()).Root

                let restoreHeader header =
                    restoreWith
                        header
                        (TaskId.token root.Structure.Task)
                        (OperationRoot.structureBytes root)
                        (OperationRoot.effectBytes root)
                        root.Structure.Compartments
                        root.Hash

                for header in
                    [
                        { root.Header with
                            InstallationId = Guid.Parse "00000000-0000-4000-8000-000000000099"
                        }
                        { root.Header with
                            PreviousHash = EntryHash.zero
                        }
                        { root.Header with
                            OperationId = ClaimCore.Tests.TaskBuilders.operation 99
                        }
                        { root.Header with
                            ActorId =
                                ActorId.ofOperationId (ClaimCore.Tests.TaskBuilders.operation 99)
                        }
                        { root.Header with
                            RecordedAt = root.Header.RecordedAt.AddTicks 10L
                        }
                    ] do
                    Expect.equal
                        (restoreHeader header)
                        (Error OperationRootProblem.Hash)
                        "Header mutation reaches hash comparison"

                Expect.equal
                    (restoreWith
                        root.Header
                        "people.disable"
                        (OperationRoot.structureBytes root)
                        (OperationRoot.effectBytes root)
                        []
                        root.Hash)
                    (Error OperationRootProblem.Hash)
                    "Task is bound"

                let structure = OperationRoot.structureBytes root |> read

                let binding =
                    BindingCodecs.binding.Write(
                        Binding.People(
                            ActorId.ofOperationId (ClaimCore.Tests.TaskBuilders.operation 99),
                            Revision.first
                        )
                    )

                let changed = setMember "binding" binding structure |> CanonicalWriter.bytes

                Expect.equal
                    (restoreWith
                        root.Header
                        (TaskId.token root.Structure.Task)
                        changed
                        (OperationRoot.effectBytes root)
                        []
                        root.Hash)
                    (Error OperationRootProblem.Hash)
                    "Binding is bound")
            testCase
                "effect contents and the descriptor remain committed after private erasure"
                (fun () ->
                    let record = personRecord ()
                    let root = record.Root
                    let rows = OperationRoot.effectBytes root

                    let changedRows =
                        rows
                        |> List.map (fun (index, bytes) ->
                            index,
                            (bytes
                             |> read
                             |> setMember "revision" (JsonValue.Integer 2L)
                             |> CanonicalWriter.bytes))

                    Expect.equal
                        (restoreWith
                            root.Header
                            (TaskId.token root.Structure.Task)
                            (OperationRoot.structureBytes root)
                            changedRows
                            root.Structure.Compartments
                            root.Hash)
                        (Error OperationRootProblem.Hash)
                        "Public part revision is committed"

                    let compartment = root.Structure.Compartments.Head

                    let wrong =
                        CompartmentDescriptor.create
                            0
                            (CompartmentOwner.Profile(
                                ActorId.ofOperationId (ClaimCore.Tests.TaskBuilders.operation 99)
                            ))
                        |> required

                    let reassigned =
                        Compartment.restore wrong compartment.Commitment None None |> required

                    Expect.equal
                        (restoreWith
                            root.Header
                            (TaskId.token root.Structure.Task)
                            (OperationRoot.structureBytes root)
                            rows
                            [ reassigned ]
                            root.Hash)
                        (Error(
                            OperationRootProblem.Structure OperationStructureProblem.Compartments
                        ))
                        "Owner substitution is independently rejected by the root"

                    Expect.isOk
                        (restore (clear record).Root)
                        "Legitimate erasure is the positive control")
            testCase
                "stored effect ordinals cannot disappear, repeat, reorder or leave gaps"
                (fun () ->
                    let root = (compoundReminder ()).Root

                    let a, b =
                        match OperationRoot.effectBytes root with
                        | [ a; b ] -> a, b
                        | _ -> failtest "Expected two parts"

                    for rows in [ []; [ a; a ]; [ b; a ]; [ a; (2, snd b) ] ] do
                        Expect.equal
                            (restoreWith
                                root.Header
                                (TaskId.token root.Structure.Task)
                                (OperationRoot.structureBytes root)
                                rows
                                root.Structure.Compartments
                                root.Hash)
                            (Error OperationRootProblem.EffectOrdinals)
                            "Exact ordinal failure"

                    Expect.isError
                        (restoreWith
                            root.Header
                            (TaskId.token root.Structure.Task)
                            (OperationRoot.structureBytes root)
                            [ a ]
                            root.Structure.Compartments
                            root.Hash)
                        "A trailing member cannot disappear")
            testCase
                "unknown members and trailing bytes are refused before hash comparison"
                (fun () ->
                    let root = (publicRecord ()).Root

                    let malformed =
                        OperationRoot.structureBytes root
                        |> read
                        |> setMember "unknown" JsonValue.Null
                        |> CanonicalWriter.bytes

                    for bytes in
                        [
                            malformed
                            Array.append
                                (OperationRoot.structureBytes root)
                                (Encoding.UTF8.GetBytes "{}")
                        ] do
                        match
                            restoreWith
                                root.Header
                                (TaskId.token root.Structure.Task)
                                bytes
                                (OperationRoot.effectBytes root)
                                []
                                root.Hash
                        with
                        | Error(OperationRootProblem.PublicMetadata _) -> ()
                        | other -> failtestf "Expected structural metadata rejection: %A" other)
            testCase
                "append refuses public metadata that its own restore limit cannot read"
                (fun () ->
                    let root = (publicRecord ()).Root

                    let retained =
                        InputMap.create
                            (Map.ofList
                                [
                                    TaskInputId.Target InputTarget.Reason,
                                    InputValue.Text(
                                        String.replicate
                                            (OperationRoot.MaximumPublicMetadataBytes + 1)
                                            "x"
                                    )
                                ])
                            Map.empty
                        |> Option.get

                    match
                        OperationRoot.append
                            root.Header
                            (TaskId.ofLiteral "sample.change")
                            root.Structure.Binding
                            retained
                            root.Structure.Effects
                            []
                    with
                    | Error(OperationRootProblem.PublicMetadata _) -> ()
                    | other ->
                        failtestf
                            "Expected bounded, restorable metadata: %A"
                            (other
                             |> Result.map (fun value ->
                                 (OperationRoot.structureBytes value).Length)))
        ]
