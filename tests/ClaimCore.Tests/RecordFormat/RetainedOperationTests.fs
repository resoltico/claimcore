module ClaimCore.Tests.RetainedOperationTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Json
open ClaimCore.RecordFormat
open ClaimCore.Tests.RecordBuilders

let private payload (record: RetainedOperation) =
    record.Root.Structure.Compartments
    |> List.exactlyOne
    |> Compartment.content
    |> Option.get
    |> snd

let private parsed bytes =
    StrictJson.read Compartment.MaximumContentBytes bytes |> required

let private canonicalNode (node: JsonNode) =
    node.ToJsonString() |> Encoding.UTF8.GetBytes |> parsed |> CanonicalWriter.bytes

let private reseal bytes (record: RetainedOperation) =
    let original = record.Root.Structure.Compartments |> List.exactlyOne
    let nonce, _ = Compartment.content original |> Option.get
    let replacement = Compartment.seal original.Descriptor nonce bytes |> required
    rewriteCompartments [ replacement ] record |> required

let private edit action (record: RetainedOperation) =
    let node = JsonNode.Parse(payload record) |> nonNull
    action node
    reseal (canonicalNode node) record

[<Tests>]
let tests =
    testList
        "generic retained accepted values"
        [
            testCase "attachment descriptors retain identity and intent only" (fun () ->
                let blobId = Guid.Parse "10000000-0000-7000-8000-000000000001"
                let blob = AttachmentId.create blobId |> Option.get
                let name = AttachmentName.parse "Evidence.pdf" |> Option.get
                let digestText = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                let digest = AttachmentDigest.parse digestText |> Option.get
                let intent = AttachmentIntent.create name 0L digest |> Option.get

                let effect =
                    PartValue.Attachment(
                        AttachmentEffect.Added(true, Some { Blob = blob; Intent = intent })
                    )

                let vector =
                    JsonValue.Object
                        [
                            "blob", JsonValue.String "10000000-0000-7000-8000-000000000001"
                            "name", JsonValue.String "Evidence.pdf"
                            "length", JsonValue.Integer 0L
                            "digest", JsonValue.String digestText
                        ]

                Expect.equal
                    (PartCodecs.privateValue blobId effect)
                    (Ok(Some vector))
                    "Format 8 descriptor"

                let codec = PartCodecs.privateValueCodec blobId effect
                Expect.equal (codec.Read "$" vector) (Ok effect) "The descriptor round-trips"

                match vector with
                | JsonValue.Object fields ->
                    Expect.isError
                        (codec.Read
                            "$"
                            (JsonValue.Object(fields @ [ "preview", JsonValue.String "PNG" ])))
                        "The earlier preview field is refused"
                | _ -> failtest "Expected an attachment descriptor object")
            testCase
                "live records keep structured inputs and accepted values without reparsing"
                (fun () ->
                    let record = caseRecord ()

                    Expect.isTrue
                        (obj.ReferenceEquals(
                            record.Inputs.Value,
                            ClaimCore.Tests.TaskBuilders.openingInputs
                        ))
                        "Original validated input structure"

                    Expect.isTrue
                        (obj.ReferenceEquals(
                            record.Changes.Head.Value,
                            ClaimCore.Tests.TaskBuilders.initialChange.Value
                        ))
                        "Original accepted value"

                    let node = parsed (payload record)

                    match node with
                    | ClaimCore.Json.JsonValue.Object members ->
                        Expect.equal
                            (members |> List.map fst |> Set.ofList)
                            (set [ "inputs"; "values" ])
                            "One generic payload grammar"
                    | _ -> failtest "Expected owned payload object"

                    let raw = payload record |> Encoding.UTF8.GetString

                    for field in [ "operationId"; "aggregate"; "revision"; "binding" ] do
                        Expect.isFalse
                            (raw.Contains("\"" + field + "\"", StringComparison.Ordinal))
                            "Private values do not duplicate root metadata")
            testCase
                "payments retain the selected entry while public part identity supplies its ID"
                (fun () ->
                    let record = paymentRecord ()
                    let restored = RetainedOperation.restore record.Root |> required

                    let payment =
                        restored.Changes |> List.find (fun part -> part.Kind = PartKind.Payment)

                    match payment.Value with
                    | PartValue.Payment { Entry = Some entry } ->
                        Expect.equal
                            (PaymentEntry.id entry)
                            record.Root.Header.OperationId
                            "Recording operation is payment identity"

                        Expect.equal
                            payment.PartId
                            (OperationId.value (PaymentEntry.id entry))
                            "Public part ID"

                        Expect.equal
                            (PaymentEntry.amount entry |> Amount.format)
                            "100"
                            "Typed accepted amount"
                    | _ -> failtest "Expected available accepted entry"

                    Expect.isFalse
                        ((payload record |> Encoding.UTF8.GetString)
                            .Contains(
                                OperationId.format record.Root.Header.OperationId,
                                StringComparison.Ordinal
                            ))
                        "Entry ID is not duplicated in private content"

                    let removed = paymentRemoval () |> clear

                    match
                        removed.Changes
                        |> List.find (fun part -> part.Kind = PartKind.Payment)
                        |> _.Value
                    with
                    | PartValue.Payment value ->
                        Expect.isTrue value.Removed "Removal survives erasure"
                        Expect.isNone value.Entry "Entry is explicitly unavailable"
                    | _ -> failtest "Expected removed payment")
            testCase
                "semantic finance and canonical private byte admission are independent controls"
                (fun () ->
                    let record = caseRecord ()

                    let corrupt =
                        edit
                            (fun node ->
                                let values = node["values"] |> nonNull
                                let case = values["0"] |> nonNull
                                let finance = case["finance"] |> nonNull
                                finance["paidAmount"] <- JsonValue.Create "1")
                            record

                    match RetainedOperation.restore corrupt with
                    | Error(RetainedOperationProblem.Content _) -> ()
                    | other ->
                        failtestf "Expected contradictory permanent finance rejection: %A" other

                    let noncanonical = reseal (Array.append [| byte ' ' |] (payload record)) record

                    Expect.isOk
                        (restore noncanonical)
                        "Hash and commitment are valid for the seeded noncanonical bytes"

                    match RetainedOperation.restore noncanonical with
                    | Error(RetainedOperationProblem.Content _) -> ()
                    | other ->
                        failtestf
                            "Expected exact private canonical-byte admission: %A"
                            (other |> Result.map (fun _ -> "accepted")))
            testCase "public-only erasure and note removal never request a private nonce" (fun () ->
                for record in [ erasedCase (); noteRemoval (); publicRecord () ] do
                    let mutable requested = false

                    let recreated =
                        RetainedOperation.create
                            record.Root.Header
                            record.Root.Structure.Task
                            record.Root.Structure.Binding
                            record.Inputs.Value
                            record.Changes
                            (fun () ->
                                requested <- true
                                Array.zeroCreate 32)
                        |> required

                    Expect.isFalse requested "No private bytes exist"

                    Expect.isEmpty
                        recreated.Root.Structure.Compartments
                        "No empty private compartment"

                    Expect.isSome (clear recreated).Inputs "Entirely retained input remains known")
            testCase
                "profile erasure retains principal and sparse grants without inventing a profile"
                (fun () ->
                    let record = personRecord ()
                    let erased = clear record

                    Expect.isNone
                        erased.Inputs
                        "The complete authored profile input is unavailable"

                    match erased.Changes.Head.Value with
                    | PartValue.Person value ->
                        Expect.isSome value.Principal "Principal is retained"
                        Expect.isTrue value.Enabled "Enabled flag is retained"

                        Expect.equal
                            value.Profile
                            (ProfileValue.Present None)
                            "Present but erased is distinct from absent"
                    | _ -> failtest "Expected a person part"

                    let disabled = disabledPerson () |> clear

                    Expect.equal
                        disabled.Inputs
                        (Some InputMap.empty)
                        "Public disable intent stays known even when its carried profile is erased"

                    match disabled.Changes.Head.Value with
                    | PartValue.Person value ->
                        Expect.isFalse value.Enabled "Public disable survives"

                        Expect.equal
                            value.Profile
                            (ProfileValue.Present None)
                            "No fabricated private fields"
                    | _ -> failtest "Expected disabled person")
            testCase
                "work and note private values inherit their identities from public parts"
                (fun () ->
                    for record in [ workRecord (); noteRecord () ] do
                        Expect.equal
                            (RetainedOperation.restore record.Root)
                            (Ok record)
                            "Typed values round trip"

                        let privateBytes = payload record |> Encoding.UTF8.GetString

                        for field in [ "caseId"; "noteId"; "revision" ] do
                            Expect.isFalse
                                (privateBytes.Contains(
                                    "\"" + field + "\"",
                                    StringComparison.Ordinal
                                ))
                                "No duplicated structural identity"

                        let erased = clear record

                        Expect.equal
                            erased.Changes.Head.PartId
                            record.Changes.Head.PartId
                            "Retained part identity")
            testCase
                "one family compartment carries both typed results of a compound reminder"
                (fun () ->
                    let record = compoundReminder ()

                    Expect.equal
                        record.Root.Structure.Compartments.Length
                        1
                        "One owner compartment"

                    Expect.equal
                        (record.Root.Structure.Effects |> List.map _.Compartment)
                        [ Some 0; Some 0 ]
                        "Both results point to it"

                    let restored = RetainedOperation.restore record.Root |> required

                    let results =
                        restored.Changes
                        |> List.map (fun part ->
                            match part.Value with
                            | PartValue.Reminder value ->
                                value.State,
                                value.Plan
                                |> Option.map (fun plan -> ReminderAction.format plan.Action)
                            | _ -> failtest "Expected reminder part")

                    Expect.equal
                        results
                        [
                            ReminderState.Completed, Some "Request photographs"
                            ReminderState.Pending, Some "Contact insurer"
                        ]
                        "Both accepted values survive"

                    let incomplete =
                        edit
                            (fun node ->
                                let values = (node["values"] |> nonNull).AsObject() in
                                values.Remove "1" |> ignore)
                            record

                    Expect.isError
                        (RetainedOperation.restore incomplete)
                        "A committed private value cannot disappear"

                    let erased = clear record

                    for part in erased.Changes do
                        match part.Value with
                        | PartValue.Reminder value ->
                            Expect.isNone value.Plan "Plans are unavailable"
                        | _ -> failtest "Expected reminder part")
            testCase "retained inputs cannot be hidden in an erasable input map" (fun () ->
                let record = caseRecord ()

                let changed =
                    edit
                        (fun node ->
                            let inputs = node["inputs"] |> nonNull

                            inputs[TaskInputId.token (
                                       TaskInputId.Target(
                                           InputTarget.Field BusinessField.CaseReference
                                       )
                                   )] <- JsonValue.Create "TASK-1")
                        record

                Expect.equal
                    (RetainedOperation.restore changed)
                    (Error RetainedOperationProblem.Inputs)
                    "Retention split is independently checked")
            testCase
                "unavailable accepted values and invalid headers refuse before nonce generation"
                (fun () ->
                    let record = caseRecord ()
                    let mutable requested = false

                    let nonce () =
                        requested <- true
                        Array.zeroCreate 32

                    let unavailable =
                        { record.Changes.Head with
                            Value = PartValue.Work None
                        }

                    Expect.isError
                        (RetainedOperation.create
                            record.Root.Header
                            record.Root.Structure.Task
                            record.Root.Structure.Binding
                            record.Inputs.Value
                            [ unavailable ]
                            nonce)
                        "Unavailable accepted bytes"

                    Expect.isFalse requested "Unavailable values do not mint a nonce"

                    Expect.isError
                        (RetainedOperation.create
                            { record.Root.Header with
                                InstallationId = Guid.Empty
                            }
                            record.Root.Structure.Task
                            record.Root.Structure.Binding
                            record.Inputs.Value
                            record.Changes
                            nonce)
                        "Invalid header"

                    Expect.isFalse requested "Invalid header does not mint a nonce")
        ]
