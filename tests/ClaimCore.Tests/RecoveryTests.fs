module ClaimCore.Tests.RecoveryTests

open System
open System.Threading
open System.Text
open Expecto
open ClaimCore.Contract
open ClaimCore.Contract.Protocol
open ClaimCore.Application
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.Tests.ApplicationFixtures

let private token = CancellationToken.None

let private rows (actor: IActorClaimsCore) =
    task {
        let! reply = actor.Pending({ AfterCursor = None; Limit = 50 }, token)

        match reply with
        | ReadReply.Succeeded page -> return page.Items
        | _ -> return failtest "Expected owned metadata."
    }

let private retainedAndForgotten _ fixture =
    task {
        let! actor = caseWorker fixture
        let operation = OperationInput.Case(openRequest 10 "RECOVERY-1")
        let! result = actor.Execute(operation, token)
        let receipt = accepted result

        let recovery =
            result.RecoveryId
            |> Option.defaultWith (fun () -> failtest "Missing journal identity")

        let! pending = rows actor

        Expect.isTrue
            (pending |> List.exists (fun item -> item.RecoveryId = recovery))
            "Terminal results remain until acknowledged"

        let! replay =
            actor.Retry(
                {
                    RecoveryId = recovery
                    OperationId = receipt.OperationId
                },
                token
            )

        Expect.equal
            replay.Outcome
            (ExecuteResult.AlreadyAccepted receipt)
            "Opaque retry executes the frozen request"

        let! forgotten = actor.Forget({ RecoveryId = recovery }, token)
        Expect.equal forgotten (ReadReply.Succeeded()) "Explicit acknowledgement"
        let! forgottenAgain = actor.Forget({ RecoveryId = recovery }, token)
        Expect.equal forgottenAgain forgotten "Idempotent forget"

        let! unavailable =
            actor.Retry(
                {
                    RecoveryId = recovery
                    OperationId = receipt.OperationId
                },
                token
            )

        Expect.equal
            unavailable.Outcome
            (ExecuteResult.Rejected Refusal.ResourceUnavailable)
            "Forget does not create replacement execution"
    }

let private rawEnvelope () =
    let input = OperationInput.Case(openRequest 10 "RAW-1")
    let raw = RequestCodecs.operationInput.Write input |> WireWriter.bytes

    let surrounded =
        Array.concat [ Encoding.UTF8.GetBytes " \n"; raw; Encoding.UTF8.GetBytes " \t" ]

    let admitted =
        ExecutionEnvelope.Admit(input, surrounded)
        |> Result.defaultWith (fun _ -> failtest "Admit whitespace")

    surrounded[0] <- 0uy
    Expect.equal admitted.Bytes[0] (byte ' ') "Input buffer copied"
    let returned = admitted.Bytes
    returned[0] <- 0uy
    Expect.equal admitted.Bytes[0] (byte ' ') "Output buffer copied"
    let foreign = OperationInput.Case(openRequest 11 "RAW-1")

    Expect.isError
        (ExecutionEnvelope.Admit(foreign, raw))
        "Typed input cannot replace frozen identity"

let private capacity _ fixture =
    task {
        let! actor = caseWorker fixture
        // Two authority grants are already journaled for the same actor.
        let! initial = rows actor

        for n in 1 .. ServiceLimits.RecoveryPerActor - initial.Length do
            let! result =
                actor.Execute(OperationInput.Case(openRequest (100 + n) $"CAP-{n}"), token)

            accepted result |> ignore

        let! refused = actor.Execute(OperationInput.Case(openRequest 999 "OVER-CAP"), token)

        Expect.equal
            refused.Outcome
            (ExecuteResult.Failed CoreFault.StoreUnavailable)
            "A full journal refuses before acceptance"

        let! absent = actor.Get({ CaseReference = "OVER-CAP" }, token)

        Expect.equal
            absent
            (ReadReply.Rejected Refusal.ResourceUnavailable)
            "No business projection on cap failure"

        let! pending = rows actor
        let! _ = actor.Forget({ RecoveryId = pending.Head.RecoveryId }, token)
        let! admitted = actor.Execute(OperationInput.Case(openRequest 999 "OVER-CAP"), token)
        accepted admitted |> ignore
    }

let private otherWorker fixture (admin: IActorClaimsCore) =
    task {
        let principal =
            PrincipalKey.human "https://issuer.example/" "recovery-identity-worker"
            |> Result.defaultWith failtest

        let! registered =
            admin.Execute(
                OperationInput.Authority(
                    authorityRequest (id 201) principal AdmittedAuthorityAct.RegisterActor
                ),
                token
            )

        acceptedAuthority registered

        let! granted =
            grant admin (id 202) principal Role.CaseHandler GrantTarget.Installation true

        acceptedAuthority granted
        return core fixture principal
    }

let private unrelatedReservation _ fixture =
    task {
        let! owner = caseWorker fixture
        let! other = otherWorker fixture owner

        let! opened =
            owner.Execute(OperationInput.Case(openRequest 203 "RESERVATION"), token)

        accepted opened |> ignore

        let pending =
            OperationInput.Case(
                request 204 "RESERVATION" 999L (CaseAct.AmendRegistration registration)
            )

        let! original = owner.Execute(pending, token)
        Expect.isSome original.RecoveryId "Original business refusal retains its submitted identity"

        let acceptedInput =
            OperationInput.Case(
                request
                    204
                    "RESERVATION"
                    1L
                    (CaseAct.AmendRegistration
                        { registration with
                            InsurerName = "Synthetic replacement"
                        })
            )

        let! acceptedOther = other.Execute(acceptedInput, token)
        let receipt = accepted acceptedOther
        let! exact = owner.Execute(acceptedInput, token)

        Expect.isTrue
            (match exact.Outcome with
             | ExecuteResult.AlreadyAccepted observed ->
                 observed.OperationId = receipt.OperationId
                 && observed.Position = receipt.Position
             | _ -> false)
            "Global accepted identity wins"

        Expect.isNone
            exact.RecoveryId
            "A different owned frozen request must never be acknowledged by this replay"

        let! retained = rows owner

        Expect.isTrue
            (retained |> List.exists (fun item -> Some item.RecoveryId = original.RecoveryId))
            "The unrelated reservation remains intact"
    }

[<Tests>]
let tests =
    testList
        "submitted request recovery"
        [
            testCase "raw envelope is validated and defensively copied" rawEnvelope
            testCaseAsync "opaque recovery and explicit acknowledgement" (run retainedAndForgotten)
            testCaseAsync "journal admission cap requires acknowledgement turnover" (run capacity)
            testCaseAsync
                "exact replay cannot acknowledge a different owned reservation"
                (run unrelatedReservation)
        ]
