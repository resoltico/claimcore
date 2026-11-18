module ClaimCore.Tests.RetainedOperationPropertyTests

open System
open System.Text
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks
open ClaimCore.Json
open ClaimCore.RecordFormat
open ClaimCore.Tests.PropertyHarness
open ClaimCore.Tests.RecordBuilders

module Tasks = ClaimCore.Tests.TaskBuilders

let private variedCase =
    property {
        let! count = Gen.int32 (Range.constant 1 80)
        let! glyph = Gen.item [ "a"; "Ā"; "ش"; "😀" ]
        let! amount = Gen.item [ "1"; "10.0000"; "999999999999999999.9999" ]
        let! sequence = Gen.item [ 1L; 2L; 1000L; Sequence.Maximum ]
        let name = String.replicate count glyph

        let values =
            Tasks.openingInputs.Values
            |> Map.add
                (TaskInputId.Target(InputTarget.Field BusinessField.PolicyNumber))
                (InputValue.Text name)
            |> Map.add
                (TaskInputId.Target(InputTarget.Field BusinessField.ClaimedAmount))
                (InputValue.Text amount)

        let input = InputMap.create values Map.empty |> Option.get
        let original = Tasks.context 1

        let context =
            { original with
                Stamp =
                    { original.Stamp with
                        Position =
                            EffectPosition.create (Sequence.create sequence |> Option.get) 0
                            |> Option.get
                    }
            }

        let selected =
            match Registry.find (TaskId.ofLiteral "case.open") with
            | Some(Task.Family task) -> task
            | _ -> failtest "Expected opening task."

        let changes =
            selected.Decide context Tasks.environment None (Binding.CaseNew Revision.zero) input
            |> required

        let record = create "case.open" (Binding.CaseNew Revision.zero) input changes
        let restored = RetainedOperation.restore record.Root |> required

        return
            restored = record
            && (match restored.Changes.Head.Value with
                | PartValue.Case { Content = Some content } ->
                    Identifier.format (Registration.policyNumber content.Registration |> Option.get) =
                        name
                    && (Registration.claimed content.Registration |> Option.get |> Money.format) =
                        (Amount.parse amount |> required |> Amount.format)
                | _ -> false)
    }

let private decoderTotality =
    property {
        let! bytes =
            Gen.list (Range.constant 1 512) (Gen.byte (Range.constant 0uy 255uy))
            |> Gen.map List.toArray

        let records = samples ()

        return
            records
            |> List.forall (fun record ->
                try
                    Codec.decode 1024 BindingCodecs.binding bytes |> ignore

                    OperationRoot.restore
                        record.Root.Header
                        (TaskId.token record.Root.Structure.Task)
                        bytes
                        (OperationRoot.effectBytes record.Root)
                        record.Root.Structure.Compartments
                        record.Root.Hash
                    |> ignore

                    match record.Root.Structure.Compartments with
                    | [ compartment ] ->
                        let nonce, _ = Compartment.content compartment |> Option.get

                        let changed =
                            Compartment.seal compartment.Descriptor nonce bytes |> required

                        let root = rewriteCompartments [ changed ] record |> required
                        RetainedOperation.restore root |> ignore
                    | _ -> ()

                    true
                with _ ->
                    false)
    }

[<Tests>]
let tests =
    testList
        "retained operation properties"
        [
            testCase
                "varied Unicode and exact money survive the case compartment across sequence bounds"
                (fun () -> run "retained Unicode and money" variedCase)
            testCase "public and private decoders are total over hostile byte sequences" (fun () ->
                run "retained decoder totality" decoderTotality)
            testCase
                "retained terminal reminder text is not readmitted as a current assertion"
                (fun () ->
                    let action = ReminderAction.parse (String.replicate 220 "x") |> required

                    Expect.isError
                        (ReminderAction.admit action)
                        "The new-input control refuses this length"

                    let original = reminderRecord ()
                    let change = original.Changes.Head

                    let change =
                        { change with
                            Value =
                                PartValue.Reminder(
                                    ReminderValue.Available(
                                        {
                                            Action = action
                                            Due = CalendarDate.parse "2026-11-16" |> required
                                            Kind = ReminderKind.FollowUp
                                        },
                                        ReminderHandling.Completed(
                                            CalendarDate.ofDate (System.DateOnly(2026, 11, 17))
                                        )
                                    )
                                )
                            Revision = Revision.create 2L |> Option.get
                        }

                    let binding =
                        Binding.Reminder(
                            CaseSelector.Id (Tasks.family ()).Id,
                            ReminderId.ofOperationId original.Root.Header.OperationId,
                            Revision.first
                        )

                    let record =
                        RetainedOperation.create
                            original.Root.Header
                            (TaskId.ofLiteral "reminder.complete")
                            binding
                            InputMap.empty
                            [ change ]
                            (fun () -> Array.create 32 7uy)
                        |> required

                    let restored = RetainedOperation.restore record.Root |> required

                    match restored.Changes.Head.Value with
                    | PartValue.Reminder(ReminderValue.Available(plan, _)) ->
                        Expect.equal
                            (ReminderAction.format plan.Action)
                            (String.replicate 220 "x")
                            "Historical text remains exact"
                    | _ -> failtest "Expected the retained terminal plan")
            testCase
                "recording instants preserve pre-epoch microseconds and equivalent offsets"
                (fun () ->
                    let record = personRecord ()

                    let at =
                        DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks 9999990L

                    let append at =
                        let header =
                            { record.Root.Header with
                                RecordedAt = at
                            }

                        let changes =
                            record.Changes
                            |> List.map (fun change ->
                                { change with
                                    Stamp = { change.Stamp with RecordedAt = at }
                                })

                        RetainedOperation.create
                            header
                            record.Root.Structure.Task
                            record.Root.Structure.Binding
                            record.Inputs.Value
                            changes
                            (fun () -> Array.create 32 9uy)

                    let first = append at |> required
                    let offset = append (at.ToOffset(TimeSpan.FromHours 2.)) |> required
                    Expect.equal first.Root.Hash offset.Root.Hash "Hash binds the UTC instant"

                    Expect.equal
                        (RetainedOperation.restore first.Root |> required)
                            .Root.Header.RecordedAt.UtcTicks
                        at.UtcTicks
                        "No pre-epoch truncation"

                    Expect.isError
                        (append (at.AddTicks 1L))
                        "Sub-microsecond instants cannot be rounded")
            testCase
                "work omission, clear and set preserve distinct inputs and accepted values"
                (fun () ->
                    let first = workRecord ()
                    let before = Tasks.apply (Tasks.family ()) first.Changes

                    let summary =
                        TaskInputId.Target(InputTarget.Field BusinessField.HandlingSummary)

                    let cases =
                        [
                            Tasks.inputs
                                [
                                    Tasks.text
                                        (InputTarget.Field BusinessField.ResponsibleActor)
                                        (ActorId.format Tasks.actorId)
                                ],
                            None,
                            Some "Private summary"
                            Tasks.inputs [ summary, InputValue.Null ], Some InputValue.Null, None
                            Tasks.inputs [ summary, InputValue.Text "Saved summary" ],
                            Some(InputValue.Text "Saved summary"),
                            Some "Saved summary"
                        ]

                    for input, authored, expected in cases do
                        let record =
                            family
                                "work.update"
                                3
                                before
                                (Binding.Work(Tasks.target before, Revision.first))
                                input

                        let restored = RetainedOperation.restore record.Root |> required

                        Expect.equal
                            (Map.tryFind summary restored.Inputs.Value.Values)
                            authored
                            "Authored presence"

                        match restored.Changes.Head.Value with
                        | PartValue.Work(Some work) ->
                            Expect.equal
                                (work.Summary |> Option.map HandlingSummary.format)
                                expected
                                "Accepted value"
                        | _ -> failtest "Expected available work")
        ]
