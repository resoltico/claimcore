module ClaimCore.Tests.RevisionExhaustionTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application.Execution
open ClaimCore.Tests.TaskBuilders

[<Tests>]
let tests =
    testCase
        "current terminal revisions remain technical failures while stale versions retain conflicts"
        (fun () ->
            let terminal = Revision.create Revision.Maximum |> Option.get
            let original = family ()

            let sequence = Sequence.create Sequence.Maximum |> Option.get

            let part =
                { initialChange with
                    Revision = terminal
                    NaturalKey = None
                    Stamp =
                        { initialChange.Stamp with
                            Position = EffectPosition.create sequence 0 |> Option.get
                            OperationId = (context 2).Stamp.OperationId
                        }
                    Value =
                        PartValue.Case
                            {
                                Reference = None
                                Lifecycle = original.Case.Value.Lifecycle
                                Handling = original.Case.Value.Handling
                                Content = original.Case.Value.Content
                                Disposition = None
                                ReopenReason = None
                            }
                }

            let state =
                CaseFamily.restore
                    original.Id
                    original.Reference
                    sequence
                    [ part ]
                    (Map.ofList [ (PartKind.Case, part.PartId), initialChange.Stamp ])
                |> accepted

            let execute expected environment =
                let binding = Binding.Case(target state, expected)

                match
                    ClaimCore.Domain.Tasks.Case.Close.decide
                        (context 3)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        binding
                        (ClaimCore.Tests.TaskBuilders.closingInputs "SETTLED")
                with
                | Error rejected -> Engine.rejection binding rejected
                | Ok _ -> failtest "A terminal case must not produce a successor"

            Expect.equal
                (execute terminal environment)
                (ExecutionStop.Failed CoreFault.InternalError)
                "Technical exhaustion is not a conflict with the same reviewed version"

            Expect.equal
                (execute Revision.first environment)
                (ExecutionStop.Refused(Refusal.revisionConflict terminal))
                "A genuinely stale version still has a conflict"

            let privateEnvironment =
                { environment with
                    Requester = AccessView.empty
                }

            Expect.equal
                (execute terminal privateEnvironment)
                (ExecutionStop.Refused(Refusal.Access AccessRefusal.ResourceUnavailable))
                "Exhaustion cannot reveal an inaccessible resource")
