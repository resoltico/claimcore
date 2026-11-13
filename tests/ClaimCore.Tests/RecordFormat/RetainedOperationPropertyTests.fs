module ClaimCore.Tests.RetainedOperationPropertyTests

open System
open System.Globalization
open System.Text
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.Tests.DecoderCorpora
open ClaimCore.Tests.PropertyHarness

let private valid result =
    Result.defaultWith (fun error -> failtestf "Retained property fixture: %A" error) result

let private guid number =
    Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)

let private operation = OperationId.create (guid 1) |> Option.get
let private parent = CaseId.ofOperationId operation
let private today = DateOnly(2026, 11, 9)

let private header =
    {
        OperationHeader.InstallationId = Guid.Parse "00000000-0000-4000-8000-000000000001"
        Sequence = Sequence.first
        PreviousHash = EntryHash.zero
        OperationId = operation
        ActorId = ActorId.create (guid 9) |> Option.get
        RecordedAt = DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)
        BusinessDate = Some today
    }

let private nonce () = [| 0uy .. 31uy |]

let private opened name amount =
    let registration =
        Inputs.registrationFrom
            [ BusinessField.ClaimantName, name; BusinessField.ClaimedAmount, amount ]

    let command =
        Case.parse
            {
                OperationId = guid 1
                CaseReference = "PROPERTY-1"
                ExpectedRevision = 0L
                Act = CaseAct.Open registration
            }
        |> valid

    let decision = Case.decideSnapshot today Map.empty command None |> valid

    let scope =
        CommandSkeleton.Case
            {
                CaseId = parent
                Reference = decision.After.Reference
                ExpectedRevision = Revision.zero
                Act = CaseActKind.Open
                SelectedPayment = None
            }

    let retained =
        RetainedOperation.create
            header
            scope
            (OperationValues.Case
                {
                    Command = command
                    Decision = decision
                })
            nonce
        |> valid

    decision, retained

let private text =
    Gen.choice
        [
            Gen.string (Range.constant 1 80) Gen.alpha
            Gen.item [ "Žąsis"; "東京"; "😀"; "é"; "مرحبا"; "quoted\"value"; "path\\value" ]
        ]

let private variedCase =
    property {
        let! name = text
        let! whole = Gen.int32 (Range.constant 1 1000000)
        let! fraction = Gen.int32 (Range.constant 0 99)

        let amount =
            whole.ToString(CultureInfo.InvariantCulture)
            + "."
            + fraction.ToString("D2", CultureInfo.InvariantCulture)

        let expected, retained = opened name amount
        let! sequence = Gen.int64 (Range.constant 1L Sequence.Maximum)

        let hdr =
            { header with
                Sequence = Sequence.create sequence |> Option.get
            }

        let root =
            OperationRoot.append
                hdr
                retained.Root.Structure.Command
                retained.Root.Structure.Effects
                retained.Root.Structure.Compartments
            |> valid

        let decoded = RetainedOperation.decode root |> valid

        return
            match decoded.Values with
            | RetainedOperationValues.Case(Some actual) ->
                actual.Decision.After = expected.After && root.Header.Sequence = hdr.Sequence
            | _ -> false
    }

let private decoderTotality =
    let _, sample = opened "Synthetic" "10.00"

    let descriptor =
        sample.Root.Structure.Compartments |> List.exactlyOne |> _.Descriptor

    let generator =
        Gen.choice
            [
                DecoderCorpora.jsonLike
                DecoderCorpora.mutated (OperationRoot.commandBytes sample.Root)
            ]

    property {
        let! raw = generator

        let total read =
            try
                read () |> ignore
                true
            with _ ->
                false

        return
            total (fun () ->
                Codec.decode
                    OperationRoot.MaximumPublicMetadataBytes
                    OperationPublicCodecs.command
                    raw)
            && total (fun () ->
                Codec.decode
                    OperationRoot.MaximumPublicMetadataBytes
                    OperationPublicCodecs.effect
                    raw)
            && total (fun () -> CaseRecord.decodeSnapshot raw)
            && total (fun () ->
                Compartment.seal header.InstallationId operation descriptor (nonce ()) raw
                |> Result.mapError (fun _ -> ())
                |> Result.bind (fun content ->
                    OperationRoot.append
                        header
                        sample.Root.Structure.Command
                        sample.Root.Structure.Effects
                        [ content ]
                    |> Result.mapError (fun _ -> ()))
                |> Result.bind (RetainedOperation.decode >> Result.mapError (fun _ -> ())))
    }

[<Tests>]
let tests =
    testList
        "retained operation boundary properties"
        [
            testCase
                "varied Unicode and exact money survive the bounded case compartment across sequence range"
                (fun () -> run "retained case Unicode and money" variedCase)
            testCase
                "current raw metadata financial and private-content decoders are total over hostile bytes"
                (fun () -> run "retained decoder totality" decoderTotality)
            testCase
                "terminal retained reminder text is not readmitted under current authoring policy"
                (fun () ->
                    let action = ReminderAction.parse (String.replicate 220 "x") |> valid

                    Expect.isError
                        (ReminderAction.admit action)
                        "A new assertion exceeds today's policy"

                    let reminderId = ReminderId.create (guid 2) |> Option.get

                    let after =
                        Reminder.ofAccepted
                            reminderId
                            parent
                            (Revision.create 2L |> Option.get)
                            action
                            (CalendarDate.parse "2026-11-09" |> valid)
                            ReminderState.Completed
                        |> valid

                    let command =
                        ReminderCommand.Change(
                            parent,
                            reminderId,
                            Revision.first,
                            ReminderChange.Complete
                        )

                    let scope =
                        CommandSkeleton.Reminder
                            {
                                CaseId = parent
                                Act = ReminderActKind.Complete
                                ReminderId = Some reminderId
                                ExpectedRevision = Some Revision.first
                            }

                    let retained =
                        RetainedOperation.create
                            header
                            scope
                            (OperationValues.Reminder
                                {
                                    Command = command
                                    Effects = ReminderEffects.Changed after
                                })
                            nonce
                        |> valid

                    match (RetainedOperation.decode retained.Root |> valid).Values with
                    | RetainedOperationValues.Reminder(Some actual) ->
                        Expect.equal
                            actual.Effects
                            (ReminderEffects.Changed after)
                            "Historical terminal action remains exact"
                    | _ -> failtest "Expected retained reminder values")
            testCase
                "recording instants preserve pre-epoch microseconds and normalize offsets"
                (fun () ->
                    let _, sample = opened "Synthetic" "10"

                    let at =
                        DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9999990L)

                    let append stamp =
                        OperationRoot.append
                            { header with RecordedAt = stamp }
                            sample.Root.Structure.Command
                            sample.Root.Structure.Effects
                            sample.Root.Structure.Compartments

                    let first = append at |> valid
                    let other = append (at.ToOffset(TimeSpan.FromHours 3.)) |> valid

                    Expect.equal
                        first.Hash
                        other.Hash
                        "Offset spelling does not change the recording instant"

                    Expect.equal first.Header.RecordedAt at "Pre-epoch microseconds remain exact"

                    Expect.isError
                        (append (at.AddTicks 1L))
                        "Sub-microsecond instants cannot be silently rounded")
            testCase
                "work preserve clear and set retain different typed meanings and bytes"
                (fun () ->
                    let actor = ActorId.create (guid 8) |> Option.get
                    let previous = HandlingSummary.parse "Previous summary" |> valid
                    let saved = HandlingSummary.parse "Saved summary" |> valid

                    let choices =
                        [
                            WorkValue.Keep, Some previous
                            WorkValue.Clear, None
                            WorkValue.Set saved, Some saved
                        ]

                    let contents =
                        choices
                        |> List.map (fun (summary, expected) ->
                            let update: ValidCaseWorkUpdate =
                                {
                                    Responsible = WorkValue.Set actor
                                    Summary = summary
                                }

                            let after =
                                {
                                    CaseWorkSnapshot.CaseId = parent
                                    Revision = Revision.create 2L |> Option.get
                                    Work =
                                        {
                                            Responsible = Some actor
                                            Summary = expected
                                        }
                                }

                            let retained =
                                RetainedOperation.create
                                    header
                                    (CommandSkeleton.Work(parent, Revision.first))
                                    (OperationValues.Work { Update = update; After = after })
                                    nonce
                                |> valid

                            match (RetainedOperation.decode retained.Root |> valid).Values with
                            | RetainedOperationValues.Work(Some actual) ->
                                Expect.equal
                                    actual.Update
                                    update
                                    "Omission differs from explicit clearing and text"

                                Expect.equal
                                    actual.After
                                    after
                                    "Independent accepted work values remain exact"
                            | _ -> failtest "Expected retained work values"

                            retained.Root.Structure.Compartments
                            |> List.exactlyOne
                            |> Compartment.content
                            |> Option.get
                            |> snd)

                    Expect.equal
                        (contents
                         |> List.map (fun bytes -> Convert.ToHexString bytes)
                         |> Set.ofList
                         |> Set.count)
                        3
                        "All three meanings have different canonical private bytes")
        ]
