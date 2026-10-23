module ClaimCore.Tests.OperationAuthorityScopeTests

open ClaimCore.Contract

open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Tests.ApplicationFixtures
open ClaimCore.TestSupport.StoreContractTests

let private prepare fixture =
    task {
        let! admin = caseWorker fixture

        for number, reference in [ 11, "CASE-A"; 12, "CASE-B" ] do
            let! outcome = admin.Execute(openRequest number reference, CancellationToken.None)
            accepted outcome |> ignore

        let principal =
            PrincipalKey.human "https://issuer.example/" "scoped-worker"
            |> Result.defaultWith failtest

        let! registered =
            admin.ExecuteAuthority(
                authorityRequest (id 20) principal AuthorityAct.RegisterActor,
                CancellationToken.None
            )

        acceptedAuthority registered

        for number, role, reference in
            [ 21, Role.CaseHandler, "CASE-A"; 22, Role.CaseReader, "CASE-B" ] do
            let! granted =
                grant admin (id number) principal role (GrantTarget.CaseReference reference) true

            acceptedAuthority granted

        return principal
    }

let private existingCaseAuthority (_: MemoryLedgerStore.Store) fixture =
    task {
        let! principal = prepare fixture

        let duplicate = request 12 "CASE-A" 1L (CaseAct.AmendRegistration registration)

        let! current = (core fixture principal).Execute(duplicate, CancellationToken.None)

        Expect.equal
            current
            (ExecuteOutcome.Rejected Refusal.IdempotencyConflict)
            "A used ID on readable CASE-B is a content conflict for a CASE-A request"

        let! read =
            fixture.Store.ReadCase(
                principal,
                Some duplicate.OperationId,
                duplicate.CaseReference,
                CancellationToken.None
            )

        let read = read |> Result.defaultWith (fun _ -> failtest "Synthetic view refused")
        let target = read.Case |> Option.get |> _.CaseId
        let actor = read.Actor

        Expect.equal actor.Cases.Count 2 "Only request and existing-operation scopes are loaded"

        let targetOnly =
            { actor with
                Cases = actor.Cases |> Map.filter (fun id _ -> id = target)
            }

        let narrowed = { read with Actor = targetOnly }

        match
            CaseDecisions.review
                fixture.InstallationId
                fixture.Calendar
                principal
                duplicate
                narrowed
        with
        | CaseReviewDecision.Refuse reason ->
            Expect.equal
                reason
                Refusal.ResourceUnavailable
                "Loading only CASE-A grants changes the existing-operation privacy decision"
        | _ -> failtest "Expected the existing-operation scope to require CASE-B authority"
    }

[<Tests>]
let tests =
    testCaseAsync
        "used operation identity can require authority on another case"
        (run existingCaseAuthority)
