module ClaimCore.TestSupport.ScopedAuthorityContract

open ClaimCore.TestSupport

open ClaimCore.Protocol
open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures

open ClaimCore.TestSupport.StoreContractReads

let internal authorityChanges fixture =
    task {
        let! owner = bootstrap fixture

        let target =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith (string >> failtest)

        do! registerReader owner target
        let! read = inspectAuthority fixture (target) (CancellationToken.None)

        match read with
        | Ok({ Actor = Some actor } as authority) ->
            Expect.equal actor.ActorId ((CaseFixtures.actorId (id 2))) "Registration identity"

            Expect.equal authority.Installation [ Role.CaseReader ] "Added grant"
        | _ -> failtest "Missing actor"

        let! removed =
            grant
                owner
                (id 4)
                target
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                false

        acceptedAuthority removed

        let! disabled =
            (Service.execute
                owner
                (OperationInput.Authority(authorityRequest (id 5) target (AuthorityAct.Disable)))
                (CancellationToken.None))

        acceptedAuthority disabled
        let! read = inspectAuthority fixture (target) (CancellationToken.None)

        match read with
        | Ok({ Actor = Some actor } as authority) ->
            Expect.isFalse actor.Enabled "Disabled"
            Expect.isEmpty authority.Installation "Removed"
        | _ -> failtest "Missing actor"
    }

let internal scopedHandler fixture (admin: ServiceContext) reference =
    task {
        let target =
            PrincipalKey.service "https://issuer.example/" "scoped-handler"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                admin
                (OperationInput.Authority(authorityRequest (id 30) target AuthorityAct.Register))
                (CancellationToken.None))

        acceptedAuthority registered

        let! granted =
            grant
                admin
                (id 31)
                target
                {
                    Role = Role.CaseHandler
                    Scope = (GrantTarget.CaseReference reference)
                }
                true

        acceptedAuthority granted
        return target, core fixture target
    }

let internal checkScopedAdvice (actor: ServiceContext) reference =
    task {
        let! result =
            (Service.get actor ({ CaseReference = reference }) (CancellationToken.None))

        match result with
        | Outcome.Succeeded value ->
            Expect.equal
                value.Advice.History
                [ HistoryDetail.Summary; HistoryDetail.Full ]
                "Case-scoped full history is advised"

            let correction =
                value.Advice.Correction
                |> Option.defaultWith (fun () -> failtest "Correct needs choices")

            Expect.contains value.Advice.Acts CaseActKind.Correct "Correction is explicitly offered"

            Expect.equal
                value.Snapshot.Revision
                (CaseFixtures.revision (2L))
                "The snapshot owns the expected revision"

            Expect.equal
                correction.Payment
                [ CorrectionGroupAction.Keep ]
                "Absent payment and absent capability require explicit KEEP"
        | _ -> failtest "Expected readable scoped case"
    }

let internal advisedCase fixture reference =
    task {
        let! admin = caseWorker fixture

        let! opened =
            (Service.execute
                admin
                (OperationInput.Case(request 20 reference 0L (AuthoredCaseAct.Open registration)))
                (CancellationToken.None))

        accepted opened |> ignore

        let decision =
            (ClaimCore.TestSupport.Inputs.decisionFrom
                [
                    BusinessField.PaymentDecisionDate, ("2026-08-15")
                    BusinessField.PayableAmount, ("750.0000")
                    BusinessField.PayableCurrency, ("EUR")
                ])

        let! decided =
            (Service.execute
                admin
                (OperationInput.Case(request 21 reference 1L (AuthoredCaseAct.Decide decision)))
                (CancellationToken.None))

        accepted decided |> ignore
        return admin
    }

let internal refusedAdvice fixture (actor: ServiceContext) pending =
    task {
        let! refused =
            (Service.execute actor (OperationInput.Case pending) (CancellationToken.None))

        Expect.equal
            refused.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ActNotPermitted))
            "Revocation is decided again under lock"

        let! current =
            (Service.get
                actor
                ({
                    CaseReference = pending.CaseReference
                })
                (CancellationToken.None))

        match current with
        | Outcome.Succeeded value ->
            Expect.equal
                value.Snapshot.Revision
                (CaseFixtures.revision (2L))
                "Refusal preserves the projection"

            Expect.equal
                value.Advice.History
                [ HistoryDetail.Summary ]
                "Fresh advice reflects revoked full history"

            Expect.isFalse
                (List.contains CaseActKind.Correct value.Advice.Acts)
                "No stale act offer"
        | _ -> failtest "Reader remains authorized"

        let! entries = history fixture pending.CaseReference (CaseFixtures.revision (0L)) 50

        Expect.equal
            (storedPage entries).Entries.Length
            2
            "Revoked execution appends no case acceptance"
    }

let internal revokedAdvice fixture =
    task {
        let reference = "SCOPED-ADVICE"
        let! admin = advisedCase fixture reference
        let! target, actor = scopedHandler fixture admin reference
        do! checkScopedAdvice actor reference

        let correction: AuthoredCorrection =
            {
                Registration =
                    Amendment.Replace(
                        ClaimCore.TestSupport.Inputs.registrationWith
                            [ BusinessField.ClaimedAmount, ("1001.0000") ]
                            (registration)
                    )
                Decision = Change.Keep
                Payment = Change.Keep
            }

        let pending = request 35 reference 2L (AuthoredCaseAct.Correct correction)

        let! preview =
            (Service.preview actor (OperationInput.Case pending) (CancellationToken.None))

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | _ -> failtest "Expected authoritative correction review"

        let! removed =
            grant
                admin
                (id 32)
                target
                {
                    Role = Role.CaseHandler
                    Scope = (GrantTarget.CaseReference reference)
                }
                false

        acceptedAuthority removed

        let! reader =
            grant
                admin
                (id 33)
                target
                {
                    Role = Role.CaseReader
                    Scope = (GrantTarget.CaseReference reference)
                }
                true

        acceptedAuthority reader
        do! refusedAdvice fixture actor pending
    }

let internal scopedWorker fixture =
    task {
        let! admin = caseWorker fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "many-scopes"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                admin
                (OperationInput.Authority(authorityRequest (id 20) principal AuthorityAct.Register))
                (CancellationToken.None))

        acceptedAuthority registered

        let! installed =
            grant
                admin
                (id 21)
                principal
                {
                    Role = Role.CaseRegistrar
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority installed

        for number in 1..12 do
            let reference = $"SCOPE-{number}"

            let! opened =
                (Service.execute
                    admin
                    (OperationInput.Case(
                        request (100 + number) reference 0L (AuthoredCaseAct.Open registration)
                    ))
                    (CancellationToken.None))

            accepted opened |> ignore

            let! granted =
                grant
                    admin
                    (id (200 + number))
                    principal
                    {
                        Role = Role.CaseReader
                        Scope = (GrantTarget.CaseReference reference)
                    }
                    true

            acceptedAuthority granted

        return principal
    }

let internal scopedAuthority fixture =
    task {
        let! principal = scopedWorker fixture

        let! read =
            inspectCase fixture (principal) (None) ("SCOPE-1") (CancellationToken.None)

        let view = read |> Result.defaultWith (fun _ -> failtest "Scoped case read")

        Expect.equal
            view.Actor.Installation
            [ Role.CaseRegistrar ]
            "Installation grants remain available"

        Expect.equal view.Actor.Cases.Count 1 "Unrelated case grants are not read"

        let! identity =
            inspectCase fixture (principal) (Some(id 102)) ("SCOPE-1") (CancellationToken.None)

        let identity = identity |> Result.defaultWith (fun _ -> failtest "Identity scopes")

        Expect.equal
            identity.Actor.Cases.Count
            2
            "Existing operation adds only its own case authority"

        let! observed =
            inspectOperation
                fixture
                (principal)
                ((CaseFixtures.operationId (id 102)))
                (CancellationToken.None)

        let observed = observed |> Result.defaultWith (fun _ -> failtest "Observe scopes")
        Expect.equal observed.Actor.Cases.Count 1 "Observation reads the accepted entry scope only"
        let! context = inspectAuthority fixture (principal) (CancellationToken.None)
        let context = context |> Result.defaultWith (fun _ -> failtest "Context read")
        Expect.isEmpty context.Cases "Context never enumerates assigned grants"
        Expect.isTrue context.HasAssignedCases "EXISTS reports assigned visibility"

        let pending =
            request 102 "SCOPE-1" 1L (AuthoredCaseAct.AmendRegistration registration)

        let! conflict =
            (Service.execute
                (Service.create
                    BuildIdentity.current.Version
                    fixture.InstallationId
                    fixture.Store
                    fixture.Calendar
                    principal)
                (OperationInput.Case pending)
                (CancellationToken.None))

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Both scopes preserve content exclusion"
    }
