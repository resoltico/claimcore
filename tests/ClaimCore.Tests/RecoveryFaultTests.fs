module ClaimCore.Tests.RecoveryFaultTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Contract
open ClaimCore.Application
open ClaimCore.Tests.ApplicationFixtures
open ClaimCore.TestSupport.StoreContractTests

type private FaultPoint =
    | ActorRead
    | CaseRead
    | EnvelopeRead

type private FaultingStore(inner: ILedgerStore, point, failure) =
    let recovery =
        { new IRecoveryStore with
            member _.StageCase(principal, envelope, decide, ct) =
                inner.Recovery.StageCase(principal, envelope, decide, ct)

            member _.StageAuthority(principal, request, envelope, decide, ct) =
                inner.Recovery.StageAuthority(principal, request, envelope, decide, ct)

            member _.Pending(actor, after, limit, ct) =
                inner.Recovery.Pending(actor, after, limit, ct)

            member _.Load(actor, request, ct) =
                if point = EnvelopeRead then
                    Task.FromResult(Error failure)
                else
                    inner.Recovery.Load(actor, request, ct)

            member _.Forget(actor, key, ct) = inner.Recovery.Forget(actor, key, ct)
            member _.Retained(actor, key, ct) = inner.Recovery.Retained(actor, key, ct)
        }

    interface ILedgerStore with
        member _.Recovery = recovery

        member _.ReadActor(principal, ct) =
            if point = ActorRead then
                Task.FromResult(Error failure)
            else
                inner.ReadActor(principal, ct)

        member _.ReadCase(principal, operation, reference, ct) =
            if point = CaseRead then
                Task.FromResult(Error failure)
            else
                inner.ReadCase(principal, operation, reference, ct)

        member _.WriteCase(principal, operation, reference, decide, ct) =
            inner.WriteCase(principal, operation, reference, decide, ct)

        member _.WriteAuthority(principal, operation, target, reference, decide, ct) =
            inner.WriteAuthority(principal, operation, target, reference, decide, ct)

        member _.ReadAuthorityTarget(principal, operation, target, reference, ct) =
            inner.ReadAuthorityTarget(principal, operation, target, reference, ct)

        member _.ReadOperation(principal, operation, ct) =
            inner.ReadOperation(principal, operation, ct)

        member _.ReadHistory(principal, reference, select, limit, ct) =
            inner.ReadHistory(principal, reference, select, limit, ct)

        member _.ListCases(principal, roles, select, limit, ct) =
            inner.ListCases(principal, roles, select, limit, ct)

        member _.ListActors(principal, select, limit, ct) =
            inner.ListActors(principal, select, limit, ct)

        member _.ListGrants(principal, target, select, limit, ct) =
            inner.ListGrants(principal, target, select, limit, ct)

let private token = CancellationToken.None
let private query = { AfterCursor = None; Limit = 50 }

let private selection =
    {
        RecoveryId = id 90
        OperationId = id 91
    }

let private failures =
    [
        StoreFailure.Unavailable, CoreFault.StoreUnavailable
        StoreFailure.Integrity, CoreFault.StoreIntegrityError
        StoreFailure.Internal, CoreFault.InternalError
        StoreFailure.SchemaMismatch, CoreFault.SchemaMismatch
        StoreFailure.Cancelled, CoreFault.StoreUnavailable
    ]

let private faulted fixture point failure =
    core
        { fixture with
            Store = FaultingStore(fixture.Store, point, failure)
        }
        fixture.Owner

let private actorFault failure expected (store: MemoryLedgerStore.Store) fixture =
    task {
        let! _ = caseWorker fixture
        let before = store.Entries.Length
        let actor = faulted fixture ActorRead failure
        let! listed = actor.Pending(query, token)

        Expect.equal
            listed
            (ReadReply.Failed expected)
            "Actor read faults are not successful empty lists"

        let! retry = actor.Retry(selection, token)

        Expect.equal
            retry.Outcome
            (ExecuteResult.Failed expected)
            "Retry cannot relabel a store fault as missing authority"

        let! forgotten = actor.Forget({ RecoveryId = selection.RecoveryId }, token)
        Expect.equal forgotten (ReadReply.Failed expected) "Forget preserves the store fault class"
        Expect.equal store.Entries.Length before "Faults dispatch no acceptance"
    }

let private pageFault point failure expected (store: MemoryLedgerStore.Store) fixture =
    task {
        let! actor = caseWorker fixture
        let! opened = actor.Execute(OperationInput.Case(openRequest 10 "FAULT-CASE"), token)
        accepted opened |> ignore
        let before = store.Entries.Length
        let broken = faulted fixture point failure
        let! listed = broken.Pending(query, token)

        Expect.equal
            listed
            (ReadReply.Failed expected)
            "Mid-page authority/protection faults are not filtered rows"

        Expect.equal store.Entries.Length before "Metadata lookup appends no accepted history"
    }

let private scopedOpen _ fixture =
    task {
        let! actor = caseWorker fixture
        let input = openRequest 10 "SCOPED-OPEN"
        let! opened = actor.Execute(OperationInput.Case input, token)
        accepted opened |> ignore

        let recoveryId =
            opened.RecoveryId
            |> Option.defaultWith (fun () -> failtest "Expected retained submission")

        let! scoped =
            grant
                actor
                (id 11)
                fixture.Owner
                Role.CaseRegistrar
                (GrantTarget.CaseReference input.CaseReference)
                true

        acceptedAuthority scoped

        let! revoked =
            grant actor (id 12) fixture.Owner Role.CaseHandler GrantTarget.Installation false

        acceptedAuthority revoked
        let! page = actor.Pending(query, token)

        let item =
            match page with
            | ReadReply.Succeeded page ->
                page.Items |> List.find (fun item -> item.RecoveryId = recoveryId)
            | _ -> failtest "Expected currently readable metadata"

        Expect.equal
            item.Offers
            [ RecoveryOffer.Forget ]
            "Case-scoped registration cannot advertise installation creation"

        let! retry =
            actor.Retry(
                {
                    RecoveryId = recoveryId
                    OperationId = input.OperationId
                },
                token
            )

        Expect.equal
            retry.Outcome
            (ExecuteResult.Rejected Refusal.ResourceUnavailable)
            "Offer and execution use the same permission"
    }

let private cursorControls () =
    let after = Guid.Parse "abcdefab-1234-4abc-8def-abcdefabcdef"
    let token = Cursors.encodeRecovery after
    Expect.equal (Cursors.decodeRecovery token) (Ok after) "Recovery position roundtrips"
    let upper = after.ToString("D").ToUpperInvariant()

    let invalid =
        [
            after.ToString "D"
            upper
            Cursors.encodeActors after
            Cursors.encodeHistory "FAULT-CASE" 1L
        ]

    for token in invalid do
        Expect.equal
            (Cursors.decodeRecovery token)
            (Error Refusal.InvalidCursor)
            "Bare and foreign-endpoint cursors refuse"

    let json = $"{{\"after\":\"{upper}\",\"kind\":\"RECOVERY\",\"v\":1}}"

    let noncanonical =
        Convert
            .ToBase64String(Encoding.UTF8.GetBytes json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    Expect.equal
        (Cursors.decodeRecovery noncanonical)
        (Error Refusal.InvalidCursor)
        "Canonical UUID spelling is required inside the token"

let private cursorAuthorization _ fixture =
    task {
        let! _ = caseWorker fixture

        let absent =
            PrincipalKey.human "https://issuer.example/" "unregistered"
            |> Result.defaultWith failtest

        let! reply =
            (core fixture absent)
                .Pending(
                    { query with
                        AfterCursor = Some "invalid"
                    },
                    token
                )

        Expect.equal
            reply
            (ReadReply.Rejected Refusal.ResourceUnavailable)
            "Actor admission precedes cursor disclosure"
    }

[<Tests>]
let tests =
    testList
        "recovery failure and advice boundaries"
        [
            for failure, expected in failures do
                testCaseAsync
                    ($"actor fault {failure} retains its classification")
                    (run (actorFault failure expected))

                testCaseAsync
                    ($"case read fault {failure} cannot hide a row")
                    (run (pageFault CaseRead failure expected))

                testCaseAsync
                    ($"protected request fault {failure} cannot remove retry silently")
                    (run (pageFault EnvelopeRead failure expected))
            testCaseAsync "case-scoped permission never advertises OPEN replay" (run scopedOpen)
            testCase "recovery cursor rejects bare and wrong-endpoint positions" cursorControls
            testCaseAsync
                "actor authorizes before malformed recovery cursor"
                (run cursorAuthorization)
        ]
