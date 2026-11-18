module ClaimCore.Tests.AggregateApplyTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates

let private accepted =
    function
    | Ok value -> value
    | Error error -> failtestf "Expected an accepted value, got %A" error

let private operation =
    OperationId.create (Guid.Parse "019aa6a0-0000-7000-8000-000000000001")
    |> Option.get

let private actor = ActorId.ofOperationId operation
let private caseId = CaseId.ofOperationId operation

let private stamp =
    {
        Position = EffectPosition.create Sequence.first 0 |> Option.get
        OperationId = operation
        Authority = Authority.Actor actor
        RecordedAt = DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero)
    }

let private reference = CaseReference.parse "APPLY-1" |> accepted

let private initial =
    {
        Aggregate = AggregateReference.Family caseId
        PartId = CaseId.value caseId
        Revision = Revision.first
        NaturalKey = Some "APPLY-1"
        Value =
            PartValue.Case
                {
                    Disposition = None
                    ReopenReason = None
                    Reference = Some reference
                    Lifecycle = Lifecycle.Active
                    Handling = Handling.Open
                    Content = None
                }
        Erases = None
        Stamp = stamp
    }

let private nextRevision = Revision.create 2L |> Option.get

let private nextStamp =
    { stamp with
        Position = EffectPosition.create (Sequence.create 2L |> Option.get) 0 |> Option.get
    }

[<Tests>]
let tests =
    testList
        "aggregate accepted-value application"
        [
            testCase "revision gaps and foreign aggregate changes are refused as values" (fun () ->
                let state = CaseFamily.apply None initial |> accepted

                let next =
                    { initial with
                        Revision = Revision.create 3L |> Option.get
                        NaturalKey = None
                        Stamp = nextStamp
                        Value =
                            PartValue.Case
                                {
                                    Disposition = None
                                    ReopenReason = None
                                    Reference = None
                                    Lifecycle = Lifecycle.Active
                                    Handling =
                                        Handling.Closed
                                            {
                                                Outcome = ClosureOutcome.Settled
                                                Note = None
                                            }
                                    Content = None
                                }
                    }

                Expect.equal
                    (CaseFamily.apply (Some state) next)
                    (Error RecordViolation.InvalidRevisionSequence)
                    "A missing version cannot be applied"

                let foreign =
                    CaseId.create (Guid.Parse "00000000-0000-4000-8000-000000000002") |> Option.get

                Expect.equal
                    (CaseFamily.apply
                        (Some state)
                        { next with
                            Aggregate = AggregateReference.Family foreign
                            Revision = nextRevision
                        })
                    (Error RecordViolation.InvalidSubjectState)
                    "The existing aggregate owns its parts"

                Expect.equal
                    (CaseFamily.apply None { initial with Revision = nextRevision })
                    (Error RecordViolation.InvalidHistoryStart)
                    "The first version is one")
            testCase
                "erasure clears all private part values without discarding retained identities"
                (fun () ->
                    let state = CaseFamily.apply None initial |> accepted
                    let noteId = Guid.Parse "019aa6a0-0000-7000-8000-000000000002"

                    let note =
                        { initial with
                            PartId = noteId
                            NaturalKey = None
                            Value =
                                PartValue.Note
                                    {
                                        Removed = false
                                        Text = Some(NoteText.parse "private note" |> accepted)
                                    }
                            Stamp =
                                { nextStamp with
                                    OperationId = OperationId.create noteId |> Option.get
                                }
                        }

                    let state = CaseFamily.apply (Some state) note |> accepted
                    let action = ReminderAction.parse "private action" |> accepted
                    let due = CalendarDate.parse "2026-11-16" |> accepted

                    let reminder =
                        { note with
                            PartId = Guid.Parse "019aa6a0-0000-7000-8000-000000000003"
                            Stamp =
                                { nextStamp with
                                    OperationId =
                                        OperationId.create (
                                            Guid.Parse "019aa6a0-0000-7000-8000-000000000003"
                                        )
                                        |> Option.get
                                }
                            Value =
                                PartValue.Reminder(
                                    ReminderValue.Available(
                                        {
                                            Action = action
                                            Due = due
                                            Kind = ReminderKind.Deadline
                                        },
                                        ReminderHandling.Pending
                                    )
                                )
                        }

                    let state = CaseFamily.apply (Some state) reminder |> accepted

                    let completed =
                        match reminder.Value with
                        | PartValue.Reminder(ReminderValue.Available(plan, _)) ->
                            { reminder with
                                Revision = nextRevision
                                Value =
                                    PartValue.Reminder(
                                        ReminderValue.Available(
                                            plan,
                                            ReminderHandling.Completed due
                                        )
                                    )
                            }
                        | _ -> failtest "A private deadline is present before erasure"

                    let state = CaseFamily.apply (Some state) completed |> accepted

                    let erase =
                        { initial with
                            Revision = nextRevision
                            NaturalKey = None
                            Value =
                                PartValue.Case
                                    {
                                        Disposition = None
                                        ReopenReason = None
                                        Reference = None
                                        Lifecycle = Lifecycle.Erased
                                        Handling = Handling.Open
                                        Content = None
                                    }
                            Erases = Some(CompartmentOwner.Family caseId)
                            Stamp = nextStamp
                        }

                    let state = CaseFamily.apply (Some state) erase |> accepted
                    Expect.equal state.Reference reference "The natural identity survives"

                    Expect.equal
                        state.Notes.Count
                        1
                        "Note identity survives without text in aggregate state"

                    Expect.equal
                        (state.Reminders |> Map.values |> Seq.exactlyOne |> _.Value.Plan)
                        None
                        "The deadline kind and the rest of the plan are unavailable"

                    Expect.isNone
                        (state.Reminders |> Map.values |> Seq.exactlyOne |> _.Value.DoneOn)
                        "Erasure clears the completion date"

                    Expect.equal
                        (state.Reminders |> Map.values |> Seq.exactlyOne |> _.Value.State)
                        ReminderState.Completed
                        "The accepted state remains retained"

                    Expect.isError
                        (CaseFamily.apply
                            (Some state)
                            { reminder with
                                Revision = nextRevision
                            })
                        "An erased aggregate cannot be changed")
            testCase "only the family's case change can erase its compartment owner" (fun () ->
                let state = CaseFamily.apply None initial |> accepted

                let wrong =
                    { initial with
                        PartId = Guid.Parse "019aa6a0-0000-7000-8000-000000000002"
                        NaturalKey = None
                        Value = PartValue.Note { Removed = true; Text = None }
                        Erases = Some(CompartmentOwner.Family caseId)
                        Stamp = nextStamp
                    }

                Expect.isError
                    (CaseFamily.apply (Some state) wrong)
                    "A note does not carry family erasure"

                Expect.isError
                    (CaseFamily.apply
                        (Some state)
                        { wrong with
                            Erases = Some(CompartmentOwner.Profile actor)
                        })
                    "Owner kinds cannot be substituted")
            testCase
                "person assignments preserve prior grant positions and keep new effects bounded"
                (fun () ->
                    let principal =
                        PrincipalKey.human "https://issuer.example/" "Subject" |> accepted

                    let root =
                        { initial with
                            Aggregate = AggregateReference.Person actor
                            PartId = ActorId.value actor
                            NaturalKey = Some(PrincipalKey.naturalKey principal)
                            Value =
                                PartValue.Person
                                    {
                                        Principal = Some principal
                                        Enabled = true
                                        Grants = []
                                        Profile = ProfileValue.Present None
                                    }
                        }

                    let state = Person.apply None root |> accepted

                    let assignments =
                        [
                            for index in 1..100 do
                                let id =
                                    CaseId.create (
                                        Guid.Parse(sprintf "00000000-0000-4000-8000-%012d" index)
                                    )
                                    |> Option.get

                                yield
                                    {
                                        Grant =
                                            {
                                                Role = Role.CaseReader
                                                Scope = Scope.Case id
                                            }
                                        Active = true
                                    }
                        ]

                    let grant =
                        { root with
                            Revision = nextRevision
                            NaturalKey = None
                            Stamp = nextStamp
                            Value =
                                PartValue.Person
                                    {
                                        Principal = None
                                        Enabled = true
                                        Grants = assignments
                                        Profile = ProfileValue.Present None
                                    }
                        }

                    let state =
                        assignments
                        |> List.mapi (fun index assignment -> index, assignment)
                        |> List.fold
                            (fun state (index, assignment) ->
                                let change =
                                    { grant with
                                        Revision =
                                            Revision.create (int64 index + 2L) |> Option.get
                                        Stamp =
                                            { nextStamp with
                                                Position =
                                                    EffectPosition.create
                                                        (Sequence.create (int64 index + 2L)
                                                         |> Option.get)
                                                        0
                                                    |> Option.get
                                            }
                                        Value =
                                            PartValue.Person
                                                {
                                                    Principal = None
                                                    Enabled = true
                                                    Grants = [ assignment ]
                                                    Profile = ProfileValue.Present None
                                                }
                                    }

                                Person.apply (Some state) change |> accepted)
                            state

                    let extra =
                        {
                            Grant =
                                {
                                    Role = Role.Auditor
                                    Scope = Scope.Installation
                                }
                            Active = true
                        }

                    let last =
                        { grant with
                            Revision = Revision.create 102L |> Option.get
                            Stamp =
                                { nextStamp with
                                    Position =
                                        EffectPosition.create
                                            (Sequence.create 102L |> Option.get)
                                            0
                                        |> Option.get
                                }
                            Value =
                                PartValue.Person
                                    {
                                        Principal = None
                                        Enabled = true
                                        Grants = [ extra ]
                                        Profile = ProfileValue.Present None
                                    }
                        }

                    let updated = Person.apply (Some state) last |> accepted
                    Expect.equal updated.Grants.Count 101 "No lifetime admission cap was invented"

                    Expect.equal
                        (Map.find assignments.Head.Grant updated.Grants)
                        nextStamp.Position
                        "Unchanged grants retain their accepting positions"

                    Expect.equal
                        (Map.find extra.Grant updated.Grants)
                        last.Stamp.Position
                        "Only the new assignment gets the current position"

                    match last.Value with
                    | PartValue.Person value ->
                        Expect.equal
                            value.Grants.Length
                            1
                            "The new accepted effect does not snapshot lifetime grants"
                    | _ -> failtest "Expected a person value")
        ]
