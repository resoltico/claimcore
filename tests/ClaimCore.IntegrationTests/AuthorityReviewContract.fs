module ClaimCore.TestSupport.AuthorityReviewContract

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

let internal authorityReview fixture principal input =
    task {
        let! preview =
            (Service.preview
                (core fixture principal)
                (OperationInput.Authority input)
                (CancellationToken.None))

        let request =
            Authority.parse input
            |> Result.defaultWith (fun _ -> failtest "Synthetic authority input must admit.")

        let! read =
            inspectTarget
                fixture
                (principal)
                {
                    OperationId = (OperationId.value (request.OperationId))
                    Target = request.Target
                    CaseReference = AuthorityRequests.targetCase request
                }
                (CancellationToken.None)

        let view =
            read |> Result.defaultWith (fun _ -> failtest "Authority snapshot must load.")

        return preview, view
    }

let internal ledgerHead fixture =
    task {
        let mutable loaded = None

        let! result: Result<Verdict<unit>, StoreFailure> =
            fixture.Store.Write(
                {
                    WriteOperation.Work =
                        (fun tx ->
                            task {
                                loaded <- Some tx.Head

                                return
                                    Verdict.Refuse(
                                        Refusal.Access AccessRefusal.ResourceUnavailable
                                    )
                            })
                    CancellationToken = CancellationToken.None
                }
            )

        Expect.equal
            result
            (Ok(Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)))
            "The head probe appends nothing."

        return loaded |> Option.get
    }

let internal authorityReviewRefusals fixture =
    task {
        let! actor = bootstrap fixture
        let! before = ledgerHead fixture

        for index, act, expected in
            [
                10,
                AuthorityAct.Register,
                (Refusal.Authority AuthorityRefusal.ActorAlreadyRegistered)
                11, AuthorityAct.Disable, (Refusal.Authority AuthorityRefusal.LastOwner)
                12,
                AuthorityAct.Revoke
                    {
                        AuthoredGrant.Role = Role.Owner
                        Scope = GrantTarget.Installation
                    },
                (Refusal.Authority AuthorityRefusal.LastOwner)
            ] do
            let input = authorityRequest (id index) fixture.Owner act
            let! preview, _ = authorityReview fixture fixture.Owner input

            Expect.equal
                preview.Outcome
                (Outcome.Rejected expected)
                "Fresh authority preview enforces the execution refusal."

            let! execution =
                (Service.execute actor (OperationInput.Authority input) (CancellationToken.None))

            Expect.equal
                execution.Outcome
                (ExecuteResult.Rejected expected)
                "Locked execution preserves the same refusal."

        let! after = ledgerHead fixture
        Expect.equal after before "Refused previews and executions leave the ledger head unchanged."
    }

let internal authorityReadIsNotManage fixture =
    task {
        let! actor = bootstrap fixture

        let auditor =
            PrincipalKey.human "https://issuer.example/" "preview-auditor"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 2) auditor AuthorityAct.Register))
                (CancellationToken.None))

        acceptedAuthority registered

        let! granted =
            grant
                actor
                (id 3)
                auditor
                {
                    Role = Role.Auditor
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted

        let input = authorityRequest (id 4) fixture.Owner (AuthorityAct.Disable)

        let! preview, view = authorityReview fixture auditor input

        Expect.isTrue
            (Authority.can auditor view.Actor Capability.ReadAuthority Scope.Installation)
            "The rejecting control really has authority read."

        Expect.equal
            preview.Outcome
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Read authority cannot authorize mutation preview."

        let! execution =
            (Service.execute
                (core fixture auditor)
                (OperationInput.Authority input)
                (CancellationToken.None))

        Expect.equal
            execution.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Preview and execution conceal the same inaccessible operation."
    }

let internal authorityReviewReplay fixture =
    task {
        let! actor = bootstrap fixture

        let target =
            PrincipalKey.human "https://issuer.example/" "preview-target"
            |> Result.defaultWith (string >> failtest)

        let input = authorityRequest (id 2) target AuthorityAct.Register
        let! before = ledgerHead fixture
        let! preview, _ = authorityReview fixture fixture.Owner input

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed(OperationReview.Authority review)) ->
            Expect.isNone review.Target "The unregistered principal has no current actor."
            Expect.equal review.Before { Enabled = false; Grants = [] } "Unregistered before state."

            Expect.equal
                review.After
                { Enabled = true; Grants = [] }
                "Registration proposes an enabled actor without grants."
        | _ -> failtest "Expected an authority proposal."

        let! after = ledgerHead fixture
        Expect.equal after before "A successful preview appends no entry."

        let! execution =
            (Service.execute actor (OperationInput.Authority input) (CancellationToken.None))

        let receipt = accepted execution

        Expect.equal
            receipt.Position.Sequence
            (CaseFixtures.sequence (Sequence.value before.Sequence + 1L))
            "Only execution advances the head."

        let! replay, _ = authorityReview fixture fixture.Owner input

        Expect.equal
            replay.Outcome
            ((Previewed.AlreadyAccepted >> Outcome.Succeeded) receipt)
            "Accepted registration replays before fresh-state refusal."

        let changed = authorityRequest (id 2) target (AuthorityAct.Disable)
        let! conflict, _ = authorityReview fixture fixture.Owner changed

        Expect.equal
            conflict.Outcome
            (Outcome.Rejected Refusal.IdempotencyConflict)
            "Changed authority content conflicts with its accepted identity."
    }

let internal grantReviewTarget fixture =
    task {
        let! actor = caseWorker fixture

        let! opened =
            (Service.execute
                actor
                (OperationInput.Case(
                    request 10 "REVIEW-CASE" 0L (AuthoredCaseAct.Open registration)
                ))
                (CancellationToken.None))

        accepted opened |> ignore

        let target =
            PrincipalKey.human "https://issuer.example/" "grant-preview-target"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 11) target AuthorityAct.Register))
                (CancellationToken.None))

        acceptedAuthority registered

        let! installed =
            grant
                actor
                (id 12)
                target
                {
                    Role = Role.CaseRegistrar
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority installed

        let! scoped =
            grant
                actor
                (id 13)
                target
                {
                    Role = Role.CaseReader
                    Scope = (GrantTarget.CaseReference "REVIEW-CASE")
                }
                true

        acceptedAuthority scoped

        let input =
            authorityRequest
                (id 14)
                target
                (AuthorityAct.Revoke
                    {
                        AuthoredGrant.Role = Role.CaseReader
                        Scope = GrantTarget.CaseReference "review-case"
                    })

        return target, input
    }

let internal assertGrantRevocation (review: AuthorityReview) =
    Expect.isTrue review.Before.Enabled "Grant changes preserve actor enabled state."

    Expect.equal
        review.Before.Grants
        [
            {
                Role = Role.CaseReader
                Scope = GrantTarget.CaseReference "REVIEW-CASE"
            }
        ]
        "Only the touched grant is shown, with the authoritative reference spelling."

    Expect.equal
        review.After
        { Enabled = true; Grants = [] }
        "The revoked grant is absent after the proposal."

    Expect.equal
        (review.Target |> Option.map _.ActorId)
        (Some((CaseFixtures.actorId (id 11))))
        "Review identifies the existing target actor."

let internal authorityGrantReview fixture =
    task {
        let! target, input = grantReviewTarget fixture
        let! before = ledgerHead fixture
        let! preview, _ = authorityReview fixture fixture.Owner input

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed(OperationReview.Authority review)) ->
            assertGrantRevocation review
        | _ -> failtest "Expected a case-grant revocation proposal."

        let! after = ledgerHead fixture
        Expect.equal after before "Grant preview writes neither ledger nor projections."

        let! targetRead =
            inspectTarget
                fixture
                (fixture.Owner)
                {
                    OperationId = id 14
                    Target = target
                    CaseReference = Some "REVIEW-CASE"
                }
                (CancellationToken.None)

        let targetRead =
            targetRead
            |> Result.defaultWith (fun _ -> failtest "Scoped authority must remain readable.")

        Expect.equal
            targetRead.Target.Installation
            [ Role.CaseRegistrar ]
            "Unrelated installation grants are untouched."

        Expect.equal
            (targetRead.Target.Cases
             |> Map.values
             |> Seq.collect (fun roles -> roles)
             |> Seq.toList)
            [ Role.CaseReader ]
            "The proposed revocation has not changed the stored case grant."
    }

let internal authorityReviewRechecks fixture =
    task {
        let! actor = bootstrap fixture

        let other =
            PrincipalKey.human "https://issuer.example/" "other-preview-owner"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 2) other AuthorityAct.Register))
                (CancellationToken.None))

        acceptedAuthority registered

        let! granted =
            grant
                actor
                (id 3)
                other
                {
                    Role = Role.Owner
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted

        let input = authorityRequest (id 4) fixture.Owner (AuthorityAct.Disable)

        let! preview, view = authorityReview fixture fixture.Owner input
        Expect.equal view.EnabledHumanOwners 2 "The preview sees two enabled owners."

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed(OperationReview.Authority _)) -> ()
        | _ -> failtest "Disabling one of two owners is initially permitted."

        let! revoked =
            grant
                actor
                (id 5)
                other
                {
                    Role = Role.Owner
                    Scope = GrantTarget.Installation
                }
                false

        acceptedAuthority revoked
        let! before = ledgerHead fixture

        let! execution =
            (Service.execute actor (OperationInput.Authority input) (CancellationToken.None))

        Expect.equal
            execution.Outcome
            (ExecuteResult.Rejected(Refusal.Authority AuthorityRefusal.LastOwner))
            "Execution decides again against its locked authority state."

        let! after = ledgerHead fixture
        Expect.equal after before "Stale preview cannot authorize a write."
    }
