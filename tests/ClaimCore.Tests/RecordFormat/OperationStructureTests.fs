module ClaimCore.Tests.OperationStructureTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Json
open ClaimCore.RecordFormat
open ClaimCore.Tests.RecordBuilders

let private structure header effects compartments (root: OperationRoot) =
    OperationStructure.create
        header
        root.Structure.Task
        root.Structure.Binding
        root.Structure.Retained
        effects
        compartments

[<Tests>]
let tests =
    testList
        "format 5 typed operation structure"
        [
            testCase
                "the eleven distinct binding shapes have shared semantic schema names"
                (fun () ->
                    let expected =
                        Map.ofList
                            [
                                BindingShape.CaseNew, "Binding.CASE.new"
                                BindingShape.Case, "Binding.CASE"
                                BindingShape.CasePayment, "Binding.CASE.payment"
                                BindingShape.CaseErase, "Binding.CASE.erase"
                                BindingShape.Work, "Binding.WORK"
                                BindingShape.ReminderNew, "Binding.REMINDER.new"
                                BindingShape.Reminder, "Binding.REMINDER"
                                BindingShape.NoteNew, "Binding.NOTE.new"
                                BindingShape.Note, "Binding.NOTE"
                                BindingShape.PeopleNew, "Binding.PEOPLE.new"
                                BindingShape.People, "Binding.PEOPLE"
                            ]

                    let records = samples ()

                    Expect.equal
                        (records
                         |> List.map (fun record ->
                             BindingShape.ofValue record.Root.Structure.Binding)
                         |> Set.ofList)
                        (expected |> Map.keys |> Set.ofSeq)
                        "The witnesses exercise every binding shape"

                    for record in records do
                        let binding = record.Root.Structure.Binding

                        let declaration =
                            ClaimCore.Domain.Tasks.Registry.findDeclaration
                                record.Root.Structure.Task
                            |> Option.get

                        let codec = BindingCodecs.forDeclaration declaration

                        Expect.equal
                            codec.Schema
                            (Schema.Ref expected[BindingShape.ofValue binding])
                            "No per-task binding schema"

                        Expect.equal
                            (Codec.encode codec binding |> Codec.decode 4096 codec)
                            (Ok binding)
                            "Typed selection and version round trip"

                    Expect.equal
                        (BindingCodecs.caseNew.Write(Binding.CaseNew Revision.zero)
                         |> CanonicalWriter.bytes
                         |> Encoding.UTF8.GetString)
                        """{"expectedRevision":0,"kind":"CASE"}"""
                        "Opening's reference belongs to inputs")
            testCase
                "headers separate family business dates from person recording instants"
                (fun () ->
                    let family, person = (caseRecord ()).Root, (personRecord ()).Root
                    Expect.isSome family.Header.BusinessDate "Family business date"

                    Expect.isNone
                        person.Header.BusinessDate
                        "People have no asserted business date"

                    for root, header in
                        [
                            family,
                            { family.Header with
                                BusinessDate = None
                            }
                            person,
                            { person.Header with
                                BusinessDate = Some(DateOnly(2026, 11, 15))
                            }
                        ] do
                        Expect.equal
                            (structure
                                header
                                root.Structure.Effects
                                root.Structure.Compartments
                                root)
                            (Error OperationStructureProblem.Effects)
                            "Aggregate/date mismatch"

                    for header in
                        [
                            { person.Header with
                                InstallationId = Guid.Empty
                            }
                            { person.Header with
                                Sequence = Sequence.zero
                            }
                            { person.Header with
                                RecordedAt = person.Header.RecordedAt.AddTicks 1L
                            }
                        ] do
                        Expect.equal
                            (structure
                                header
                                person.Structure.Effects
                                person.Structure.Compartments
                                person)
                            (Error OperationStructureProblem.Header)
                            "Exact header boundary")
            testCase
                "every part uses the root's operation, actor, timestamp, sequence and ordered index"
                (fun () ->
                    let root = (compoundReminder ()).Root
                    let first = root.Structure.Effects.Head
                    let stamp = first.Change.Stamp

                    let changed stamp =
                        { first with
                            Change = { first.Change with Stamp = stamp }
                        }
                        :: root.Structure.Effects.Tail

                    let other = ClaimCore.Tests.TaskBuilders.operation 99

                    for stamp in
                        [
                            { stamp with OperationId = other }
                            { stamp with
                                Authority = Authority.Actor(ActorId.ofOperationId other)
                            }
                            { stamp with
                                RecordedAt = stamp.RecordedAt.AddTicks 10L
                            }
                            { stamp with
                                Position =
                                    EffectPosition.create
                                        (Sequence.successor root.Header.Sequence)
                                        0
                                    |> Option.get
                            }
                            { stamp with
                                Position =
                                    EffectPosition.create root.Header.Sequence 1 |> Option.get
                            }
                        ] do
                        Expect.equal
                            (structure root.Header (changed stamp) root.Structure.Compartments root)
                            (Error OperationStructureProblem.Effects)
                            "Independent attribution mismatch"

                    let foreign =
                        { first with
                            Change =
                                { first.Change with
                                    Aggregate =
                                        AggregateReference.Family(CaseId.ofOperationId other)
                                }
                        }

                    Expect.equal
                        (structure root.Header [ first; foreign ] root.Structure.Compartments root)
                        (Error OperationStructureProblem.Effects)
                        "A root cannot span aggregates")
            testCase
                "compartment references, owners and ordinals are independently closed"
                (fun () ->
                    let root = (personRecord ()).Root
                    let compartment = root.Structure.Compartments.Head
                    let first = root.Structure.Effects.Head

                    Expect.equal
                        (structure root.Header root.Structure.Effects [] root)
                        (Error OperationStructureProblem.CompartmentBinding)
                        "Missing referenced compartment"

                    for reference in [ None; Some 1 ] do
                        Expect.equal
                            (structure
                                root.Header
                                [ { first with Compartment = reference } ]
                                root.Structure.Compartments
                                root)
                            (Error OperationStructureProblem.CompartmentBinding)
                            "Missing or foreign ordinal"

                    let nonce, payload = Compartment.content compartment |> Option.get

                    let ordinal =
                        CompartmentDescriptor.create 1 compartment.Descriptor.Owner |> required

                    let extra = Compartment.seal ordinal (Array.create 32 99uy) payload |> required

                    Expect.equal
                        (structure root.Header root.Structure.Effects [ extra ] root)
                        (Error OperationStructureProblem.Compartments)
                        "Ordinal gap"

                    Expect.equal
                        (structure root.Header root.Structure.Effects [ compartment; extra ] root)
                        (Error OperationStructureProblem.Compartments)
                        "Duplicate owner, with distinct nonce"

                    let foreign =
                        CompartmentDescriptor.create
                            0
                            (CompartmentOwner.Profile(
                                ActorId.ofOperationId (ClaimCore.Tests.TaskBuilders.operation 99)
                            ))
                        |> required

                    let foreign = Compartment.seal foreign nonce payload |> required

                    Expect.equal
                        (structure root.Header root.Structure.Effects [ foreign ] root)
                        (Error OperationStructureProblem.Compartments)
                        "Wrong owner"

                    let erased = Compartment.withoutContent compartment

                    Expect.isOk
                        (structure root.Header root.Structure.Effects [ erased ] root)
                        "Restore may have unavailable content"

                    Expect.equal
                        (OperationStructure.append
                            root.Header
                            root.Structure.Task
                            root.Structure.Binding
                            root.Structure.Retained
                            root.Structure.Effects
                            [ erased ])
                        (Error OperationStructureProblem.UnavailableAppendContent)
                        "Append cannot fabricate missing bytes")
            testCase
                "effect count bounds are technical and do not depend on task or part vocabulary"
                (fun () ->
                    let root = (publicRecord ()).Root

                    let header =
                        { root.Header with
                            BusinessDate = Some(DateOnly(2026, 11, 15))
                        }

                    let family = (ClaimCore.Tests.TaskBuilders.family ()).Id

                    let parts count =
                        List.init count (fun index ->
                            {
                                RecordedPart.Change =
                                    {
                                        Aggregate = AggregateReference.Family family
                                        PartId =
                                            OperationId.value (
                                                ClaimCore.Tests.TaskBuilders.operation (
                                                    index + 100
                                                )
                                            )
                                        Revision = Revision.create 2L |> Option.get
                                        NaturalKey = None
                                        Value = PartValue.Note { Removed = true; Text = None }
                                        Erases = None
                                        Stamp =
                                            {
                                                OperationId = header.OperationId
                                                Authority = header.Authority
                                                RecordedAt = header.RecordedAt
                                                Position =
                                                    EffectPosition.create header.Sequence index
                                                    |> Option.get
                                            }
                                    }
                                Compartment = None
                            })

                    let create count =
                        OperationStructure.create
                            header
                            (TaskId.ofLiteral "sample.change")
                            (Binding.Case(CaseSelector.Id family, Revision.first))
                            InputMap.empty
                            (parts count)
                            []

                    Expect.equal
                        (create 0)
                        (Error OperationStructureProblem.Effects)
                        "At least one effect"

                    Expect.isOk (create 50) "Exact technical effect budget"

                    Expect.equal
                        (create 51)
                        (Error OperationStructureProblem.Effects)
                        "The 51st effect refuses")
        ]
