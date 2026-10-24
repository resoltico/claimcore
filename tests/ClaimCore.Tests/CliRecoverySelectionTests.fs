module ClaimCore.Tests.CliRecoverySelectionTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Contract
open ClaimCore.Cli

let private recoveryId = Guid.Parse "90000000-0000-4000-8000-000000000100"
let private instant = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

let private selected: RecoveryItem =
    {
        RecoveryId = recoveryId
        OperationId = CliReplyFixtures.id
        Subject =
            RecoverySubject.Case
                {
                    CaseReference = "x"
                    ExpectedRevision = 0L
                    Act = CaseActKind.Business BusinessActKind.Open
                }
        CreatedAt = instant
        ExpiresAt = instant.AddDays 7.
        Offers = [ RecoveryOffer.Retry; RecoveryOffer.Forget ]
    }

let private invocation () =
    JsonValue.Object
        [
            "endpoint", JsonValue.String Api.operationRetry.Id
            "input",
            Api.operationRetry.Input.Write
                {
                    RecoveryId = recoveryId
                    OperationId = CliReplyFixtures.id
                }
        ]
    |> WireWriter.bytes
    |> Frames.admit
    |> Result.defaultWith (fun _ -> failtest "Retry frame")

let private run (pages: RecoveryPage list) receipt =
    let responses = System.Collections.Generic.Queue<RecoveryPage>(pages)
    let cursors = ResizeArray<string option>()
    let mutable attempts = 0

    let send request before _ =
        let id = Api.id (Frames.endpoint request)

        let body =
            if id = Api.operationPending.Id then
                let pageRequest =
                    Api.operationPending.Input.Read "$" (Frames.input request)
                    |> Result.defaultWith (fun _ -> failtest "Pending input")

                cursors.Add pageRequest.AfterCursor
                Api.operationPending.Output.Write(ReadReply.Succeeded(responses.Dequeue()))
            else
                Expect.equal id Api.operationRetry.Id "Only selected retry dispatches"
                attempts <- attempts + 1
                before ()

                Api.operationRetry.Output.Write
                    {
                        OperationId = CliReplyFixtures.id
                        RecoveryId = Some recoveryId
                        Outcome = ExecuteResult.AlreadyAccepted receipt
                    }

        CliReply.result request body |> Task.FromResult

    let reply =
        RecoverySelection.invoke send (invocation ()) ignore CancellationToken.None
        |> _.GetAwaiter().GetResult()

    reply, attempts, List.ofSeq cursors

let private page item =
    { Items = [ item ]; NextCursor = None }: RecoveryPage

let private caseReceipt change =
    let receipt = CliReplyFixtures.receipt

    match receipt.Scope with
    | ReceiptScope.Case value ->
        { receipt with
            Scope = ReceiptScope.Case(change value)
        }
    | _ -> failtest "Case receipt"

let private mismatches =
    [
        "act",
        caseReceipt (fun value ->
            { value with
                Act = CaseActKind.Business BusinessActKind.AmendRegistration
            })
        "reference", caseReceipt (fun value -> { value with Reference = "foreign" })
        "revision", caseReceipt (fun value -> { value with Revision = 2L })
        "scope",
        { CliReplyFixtures.receipt with
            Scope =
                ReceiptScope.Authority
                    {
                        TargetActorId = CliReplyFixtures.id
                        Act = AuthorityKind.SetEnabled
                    }
        }
    ]

[<Tests>]
let tests =
    testList
        "CLI recovery metadata correlation"
        [
            testCase "pagination follows non-null cursors after an empty page" (fun () ->
                let reply, attempts, cursors =
                    run
                        [
                            {
                                Items = []
                                NextCursor = Some "opaque-next"
                            }
                            page selected
                        ]
                        CliReplyFixtures.receipt

                Expect.equal
                    cursors
                    [ None; Some "opaque-next" ]
                    "No short-page termination assumption"

                Expect.equal attempts 1 "One explicit retry"
                Expect.equal reply.ExitCode 0 "Matching metadata resolves uncertainty")
            testCase "absent metadata never dispatches or acknowledges retry" (fun () ->
                let reply, attempts, _ =
                    run [ { Items = []; NextCursor = None } ] CliReplyFixtures.receipt

                Expect.equal attempts 0 "No request without selected metadata"
                Expect.equal reply.ExitCode 4 "Earlier uncertainty retained"
                Expect.isNone (RecoveryAcknowledgement.frame reply) "No cleanup")
            testCase
                "unavailable metadata listing never dispatches or acknowledges retry"
                (fun () ->
                    let mutable dispatched = false

                    let send _ _ _ =
                        Task.FromResult(CliReply.local ClientFailure.Unavailable)

                    let reply =
                        RecoverySelection.invoke
                            send
                            (invocation ())
                            (fun () -> dispatched <- true)
                            CancellationToken.None
                        |> _.GetAwaiter().GetResult()

                    Expect.isFalse dispatched "Pending failure cannot dispatch business execution"

                    Expect.equal
                        reply.ExitCode
                        4
                        "Availability does not resolve earlier uncertainty"

                    Expect.isNone
                        (RecoveryAcknowledgement.frame reply)
                        "No cleanup on metadata failure")
            testCase "cyclic metadata cursor is rejected without retry" (fun () ->
                let reply, attempts, cursors =
                    run
                        [
                            { Items = []; NextCursor = Some "same" }
                            { Items = []; NextCursor = Some "same" }
                        ]
                        CliReplyFixtures.receipt

                Expect.equal attempts 0 "Repeated cursor cannot dispatch"
                Expect.equal cursors [ None; Some "same" ] "Cycle detected after two pages"

                Expect.equal
                    reply.ExitCode
                    4
                    "Invalid pagination does not resolve earlier uncertainty")
            testCase "foreign operation echo never dispatches retry" (fun () ->
                let item =
                    { selected with
                        OperationId = Guid.Parse "90000000-0000-4000-8000-000000000101"
                    }

                let reply, attempts, _ = run [ page item ] CliReplyFixtures.receipt
                Expect.equal attempts 0 "Operation identity is bound before retry"
                Expect.equal reply.ExitCode 4 "No inferred refusal of earlier work")
            for name, receipt in mismatches do
                testCase ($"mismatched receipt {name} cannot resolve or acknowledge") (fun () ->
                    let reply, attempts, _ = run [ page selected ] receipt
                    Expect.equal attempts 1 "The selected attempt was sent"
                    Expect.equal reply.ExitCode 4 "Mismatched acceptance is still uncertain"

                    Expect.isNone
                        (RecoveryAcknowledgement.frame reply)
                        "No destructive acknowledgement")
            testCase "authority target actor must match selected metadata" (fun () ->
                let item =
                    { selected with
                        Subject =
                            RecoverySubject.Authority
                                {
                                    TargetActorId = CliReplyFixtures.id
                                    CaseReference = None
                                    Act = RecoveryAuthorityKind.SetEnabled
                                }
                    }

                let receipt =
                    { CliReplyFixtures.receipt with
                        Scope =
                            ReceiptScope.Authority
                                {
                                    TargetActorId = recoveryId
                                    Act = AuthorityKind.SetEnabled
                                }
                    }

                let reply, _, _ = run [ page item ] receipt
                Expect.equal reply.ExitCode 4 "Wrong target actor cannot confirm recovery")
        ]
