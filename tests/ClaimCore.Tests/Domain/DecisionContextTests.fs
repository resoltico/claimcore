module ClaimCore.Tests.DecisionContextTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport

let private accepted value =
    value |> Result.defaultWith (string >> failtest)

let private observed = DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)
let private today = DateOnly(2026, 11, 14)
let private identity number = OperationIds.numberedAt observed number

let private operation number =
    CaseFixtures.operationId (identity number)

let private context =
    { CaseFixtures.recordedContext today with
        ObservedAt = observed
        Lifetime = DecisionLifetime.Execution None
    }

let private opening =
    CaseFixtures.command
        {
            OperationId = identity 1
            CaseReference = "CONTEXT-1"
            ExpectedRevision = 0L
            Act = AuthoredCaseAct.Open Inputs.registration
        }

let private denied =
    { context with
        Access = AccessView.empty
    }

let private openingEffect () =
    CaseSubject.decide context None opening |> accepted

let private parent () =
    CaseSubject.apply
        {
            Position = EffectPosition.create Sequence.first 0 |> Option.get
            OperationId = opening.OperationId
            ActorId = context.Access.Actor.Value.ActorId
            RecordedAt = observed
        }
        None
        (openingEffect ())
    |> Option.get

let private reader =
    { context with
        Access =
            { context.Access with
                Installation = [ Role.CaseReader ]
            }
    }

[<Tests>]
let tests =
    testList
        "complete subject decision context"
        [
            testCase
                "case decisions use access, maintenance and execution lifetime from their context"
                (fun () ->
                    let effect: Result<CaseDecision, Refusal> =
                        CaseSubject.decide context None opening

                    Expect.equal
                        (effect |> Result.map (fun value -> value.After.Revision))
                        (Ok Revision.first)
                        "Allowed context opens exactly the first revision"

                    Expect.equal
                        (CaseSubject.decide denied None opening)
                        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
                        "Unknown executors cannot decide"

                    Expect.equal
                        (CaseSubject.decide { context with Maintenance = true } None opening)
                        (Error Refusal.RegisterMaintenance)
                        "The subject owns maintenance admission"

                    match
                        CaseSubject.decide
                            { context with
                                ObservedAt = observed.AddMinutes 11.
                            }
                            None
                            opening
                    with
                    | Error(Refusal.OperationIdOutOfWindow _) -> ()
                    | other -> failtestf "Expected current lifetime refusal: %A" other

                    match
                        CaseSubject.decide
                            { context with
                                Lifetime =
                                    DecisionLifetime.Execution(Some(observed.AddMilliseconds 1.))
                            }
                            None
                            opening
                    with
                    | Error(Refusal.OperationClockUnavailable _) -> ()
                    | other -> failtestf "Expected durable frontier refusal: %A" other)
            testCase "denied registration does not reveal whether the case exists" (fun () ->
                let refusal = Error(Refusal.Access AccessRefusal.ResourceUnavailable)

                Expect.equal
                    (CaseSubject.decide reader None opening |> Result.map ignore)
                    refusal
                    "An unknown creation target stays opaque"

                Expect.equal
                    (CaseSubject.decide reader (Some(parent ())) opening |> Result.map ignore)
                    refusal
                    "An existing creation target has the same denied result")
            testCase
                "preview and recorded facts do not import the current execution window"
                (fun () ->
                    Expect.isOk
                        (CaseSubject.decide
                            { context with
                                ObservedAt = observed.AddMinutes 11.
                                Lifetime = DecisionLifetime.Preview None
                            }
                            None
                            opening)
                        "Preview applies the frontier without the commit window"

                    let recorded =
                        { context with
                            ObservedAt = observed.AddDays 1.
                            Lifetime = DecisionLifetime.Recorded
                            Maintenance = true
                        }

                    Expect.isOk
                        (CaseSubject.decide recorded None opening)
                        "Accepted historical facts do not apply today's operational pause or expiry"

                    Expect.equal
                        (CaseSubject.decide
                            { recorded with
                                Access = AccessView.empty
                            }
                            None
                            opening)
                        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
                        "Historical decisions still use the executor's replayed access")
            testCase
                "work, reminder and note decisions enforce the same context boundary"
                (fun () ->
                    let parent = parent ()
                    let work = Some(WorkSubject.initial parent.CaseId)

                    let update =
                        {
                            ExpectedRevision = Revision.zero
                            Update =
                                {
                                    ValidCaseWorkUpdate.Responsible = WorkValue.Keep
                                    Summary =
                                        WorkValue.Set(HandlingSummary.parse "Handling" |> accepted)
                                }
                        }

                    let deps =
                        {
                            WorkSubjectDependencies.Parent = parent
                            AssigneeEligible = fun _ -> true
                        }

                    let workDecision context =
                        WorkSubject.decide context (operation 2) deps work update
                        |> Result.map ignore

                    let reminder =
                        ReminderCommand.Create(
                            parent.CaseId,
                            {
                                Action = ReminderAction.parse "Call claimant" |> accepted
                                Due = CalendarDate.parse "2026-11-20" |> accepted
                            }
                        )

                    let reminderDecision context =
                        ReminderSubject.decide
                            context
                            (operation 3)
                            {
                                Parent = parent
                                IdentityReserved = false
                            }
                            None
                            reminder
                        |> Result.map ignore

                    let note =
                        NoteCommand.AddNote(
                            parent.CaseId,
                            NoteText.parse "Context note" |> accepted
                        )

                    let noteDecision context =
                        NoteSubject.decide context (operation 4) parent None note
                        |> Result.map ignore

                    for decide in [ workDecision; reminderDecision; noteDecision ] do
                        Expect.equal (decide context) (Ok()) "The actual allowed context proceeds"

                        Expect.equal
                            (decide reader)
                            (Error(
                                Refusal.Access(
                                    AccessRefusal.NotAuthorized Capability.ManageCaseWork
                                )
                            ))
                            "Readable state does not confer mutation rights"

                        Expect.equal
                            (decide { context with Maintenance = true })
                            (Error Refusal.RegisterMaintenance)
                            "The subject rejects an operational pause"

                        Expect.equal
                            (decide denied)
                            (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
                            "Unreadable resources stay opaque")
            testCase
                "summary-only work updates keep an assignee who is no longer eligible"
                (fun () ->
                    let parent = parent ()

                    let assignee =
                        CaseFixtures.actorId (Guid.Parse "30000000-0000-4000-8000-000000000003")

                    let initial = WorkSubject.initial parent.CaseId

                    let before =
                        { initial with
                            Revision = Revision.first
                            Snapshot =
                                Some
                                    {
                                        CaseId = parent.CaseId
                                        Revision = Revision.first
                                        Work =
                                            {
                                                Responsible = Some assignee
                                                Summary =
                                                    Some(
                                                        HandlingSummary.parse "Existing summary"
                                                        |> accepted
                                                    )
                                            }
                                    }
                        }

                    let dependencies =
                        {
                            WorkSubjectDependencies.Parent = parent
                            AssigneeEligible = fun _ -> false
                        }

                    let decide responsible =
                        WorkSubject.decide
                            context
                            (operation 2)
                            dependencies
                            (Some before)
                            {
                                ExpectedRevision = Revision.first
                                Update =
                                    {
                                        Responsible = responsible
                                        Summary =
                                            WorkValue.Set(
                                                HandlingSummary.parse "Edited summary" |> accepted
                                            )
                                    }
                            }

                    Expect.isOk
                        (decide WorkValue.Keep)
                        "Omission preserves the existing assignment"

                    Expect.equal
                        (decide (WorkValue.Set assignee))
                        (Error(Refusal.Case CaseRefusal.AssigneeUnavailable))
                        "Explicit assignment still checks current eligibility")
            testCase
                "people decisions take requester access and lifetime from the same context"
                (fun () ->
                    let principal =
                        PrincipalKey.human "https://issuer.example/" "new-person" |> accepted

                    let command =
                        People.parse
                            {
                                OperationId = identity 5
                                Target = PrincipalKey.toInput principal
                                Act = PeopleAct.Register Inputs.actorProfile
                            }
                        |> accepted

                    let deps =
                        {
                            PeopleSubjectDependencies.TargetCases = Map.empty
                            EnabledHumanOwners = Some 1
                            ActorCount = Some 1
                        }

                    Expect.isOk
                        (PeopleSubject.decide context deps None command)
                        "A manager may register a person"

                    Expect.equal
                        (PeopleSubject.decide reader deps None command |> Result.map ignore)
                        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
                        "A reader cannot make a people decision"

                    Expect.equal
                        (PeopleSubject.decide { context with Maintenance = true } deps None command
                         |> Result.map ignore)
                        (Error Refusal.RegisterMaintenance)
                        "People follows the same pause boundary")
            testCase "erased application still requires the replayed executor's access" (fun () ->
                let structure =
                    {
                        CaseSubjectStructure.Stamp =
                            {
                                Position = EffectPosition.create Sequence.first 0 |> Option.get
                                OperationId = opening.OperationId
                                ActorId = context.Access.Actor.Value.ActorId
                                RecordedAt = observed
                            }
                        CaseId = CaseId.ofOperationId opening.OperationId
                        Reference = Some opening.Reference
                        Revision = Revision.first
                        Act = CaseActKind.Open
                        RemovedPayment = None
                    }

                let recorded =
                    { context with
                        Lifetime = DecisionLifetime.Recorded
                    }

                Expect.isOk
                    (CaseSubject.applyErased recorded None structure)
                    "Structural history applies with its actual accepting rights"

                Expect.isError
                    (CaseSubject.applyErased
                        { recorded with
                            Access = AccessView.empty
                        }
                        None
                        structure)
                    "Missing content cannot bypass authorization")
        ]
