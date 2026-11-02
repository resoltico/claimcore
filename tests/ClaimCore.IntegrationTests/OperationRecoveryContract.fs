module ClaimCore.TestSupport.OperationRecoveryContract

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let internal fabricatedFenceIsNotProof fixture =
    task {
        let! actor = caseWorker fixture

        let key =
            {
                FenceKey.ActorId = CaseFixtures.actorId (id 1)
                OperationId = CaseFixtures.operationId (id 80)
            }

        let! injected =
            fixture.Store.Write(
                {
                    Work =
                        fun tx ->
                            task {
                                do! tx.Fence key
                                return Verdict.Proceed()
                            }
                    CancellationToken = CancellationToken.None
                }
            )

        Expect.equal
            injected
            (Ok(Verdict.Proceed()))
            "The corruption control actually installs a projection without retained control"

        let! settled =
            Service.resolve actor { OperationLookup.OperationId = id 80 } CancellationToken.None

        Expect.equal
            settled.Outcome
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "A fabricated projection cannot prove a permanent promise"

        let! executed =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request 80 "FABRICATED-FENCE" 0L (AuthoredCaseAct.Open registration)
                ))
                CancellationToken.None

        Expect.equal
            executed.Outcome
            (ExecuteResult.Failed CoreFault.StoreIntegrityError)
            "Execution cannot treat a fabricated projection as authoritative"
    }

let internal passiveStatusAndRolelessAuthorship fixture =
    task {
        let! owner = caseWorker fixture

        let target =
            PrincipalKey.human "https://issuer.example/" "recovering-without-roles"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            Service.execute
                owner
                (OperationInput.Authority(authorityRequest (id 10) target AuthorityAct.Register))
                CancellationToken.None

        acceptedAuthority registered

        let! granted =
            grant
                owner
                (id 11)
                target
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted
        let author = core fixture target

        let! opened =
            Service.execute
                author
                (Inputs.caseOperation (
                    request 20 "PASSIVE-STATUS" 0L (AuthoredCaseAct.Open registration)
                ))
                CancellationToken.None

        accepted opened |> ignore

        let! revoked =
            grant
                owner
                (id 21)
                target
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                false

        acceptedAuthority revoked

        let readOnly =
            { new IStore with
                member _.Read operation = fixture.Store.Read operation

                member _.Write _ =
                    failtest "Passive recovery must never request a write transaction"
            }

        let passive = core { fixture with Store = readOnly } target

        let! known =
            Service.status passive { OperationLookup.OperationId = id 20 } CancellationToken.None

        match known.Outcome with
        | Outcome.Succeeded(OperationStatus.Accepted receipt) ->
            Expect.equal
                receipt.OperationId
                (CaseFixtures.operationId (id 20))
                "Authorship preserves exact acceptance identity"

            match receipt.Subject with
            | ReceiptSubject.Case value ->
                Expect.equal
                    value.Snapshot
                    SnapshotDisclosure.Withheld
                    "Roleless authors get metadata without current case content"
            | _ -> failtest "Expected the author's case receipt"
        | _ -> failtest "A valid roleless author can read its accepted status"

        let! ordinary =
            Service.observe passive { OperationLookup.OperationId = id 20 } CancellationToken.None

        Expect.equal
            ordinary.Outcome
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "General observation keeps its separate permission boundary"

        let! unresolved =
            Service.status passive { OperationLookup.OperationId = id 30 } CancellationToken.None

        Expect.equal
            unresolved.Outcome
            (Outcome.Succeeded OperationStatus.Unresolved)
            "A negative read cannot become final nonacceptance"

        let key =
            {
                FenceKey.ActorId = CaseFixtures.actorId (id 10)
                OperationId = CaseFixtures.operationId (id 30)
            }

        let! evidence =
            readOnly.Read
                {
                    Work = fun tx -> tx.NonAcceptance key
                    CancellationToken = CancellationToken.None
                }

        Expect.equal
            evidence
            (Ok { Control = None; ProjectedAt = None })
            "Status creates neither a retained control nor a fence projection"

        let! foreign =
            Service.status
                (core { fixture with Store = readOnly } fixture.Owner)
                { OperationLookup.OperationId = id 20 }
                CancellationToken.None

        Expect.equal
            foreign.Outcome
            (Outcome.Succeeded OperationStatus.Unresolved)
            "Even a privileged reader cannot recover another actor's raw operation UUID as its own"

        let! final =
            Service.resolve author { OperationLookup.OperationId = id 30 } CancellationToken.None

        Expect.equal
            final.Outcome
            (Outcome.Succeeded Resolved.NotAccepted)
            "Explicit roleless settlement still records a permanent promise"

        let! negative =
            Service.status passive { OperationLookup.OperationId = id 30 } CancellationToken.None

        Expect.equal
            negative.Outcome
            (Outcome.Succeeded OperationStatus.NotAccepted)
            "A passive read can recognize its own committed final promise"

        let! foreignNegative =
            Service.status
                (core { fixture with Store = readOnly } fixture.Owner)
                { OperationLookup.OperationId = id 30 }
                CancellationToken.None

        Expect.equal
            foreignNegative.Outcome
            (Outcome.Succeeded OperationStatus.Unresolved)
            "Final promises are actor-scoped too"

        let! disabled =
            Service.execute
                owner
                (OperationInput.Authority(authorityRequest (id 31) target AuthorityAct.Disable))
                CancellationToken.None

        acceptedAuthority disabled

        let! denied =
            Service.status passive { OperationLookup.OperationId = id 30 } CancellationToken.None

        Expect.equal
            denied.Outcome
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Disabled actors retain the denial policy"
    }

let internal acceptanceAndFenceContradiction fixture =
    task {
        let! actor = caseWorker fixture

        let input =
            Inputs.caseOperation (
                request 80 "ACCEPTED-AND-FENCED" 0L (AuthoredCaseAct.Open registration)
            )

        let! opened = Service.execute actor input CancellationToken.None
        accepted opened |> ignore

        let key =
            {
                FenceKey.ActorId = CaseFixtures.actorId (id 1)
                OperationId = CaseFixtures.operationId (id 80)
            }

        let! injected =
            fixture.Store.Write
                {
                    Work =
                        fun tx ->
                            task {
                                let control =
                                    LedgerEntries.create
                                        {
                                            InstallationId = fixture.InstallationId
                                            Head = tx.Head
                                            Instant = tx.ObservedAt
                                            Nonce = tx.Nonce
                                            ActorId = key.ActorId
                                            OperationId = key.OperationId
                                            Subject = EntryTarget.NonAcceptance key.OperationId
                                            Request = ControlRecord.encode ()
                                            Snapshot = Array.empty
                                        }

                                do! tx.Append control
                                do! tx.Fence key
                                return Verdict.Proceed()
                            }
                    CancellationToken = CancellationToken.None
                }

        Expect.equal
            injected
            (Ok(Verdict.Proceed()))
            "The contradictory control and projection were genuinely committed"

        let! status =
            Service.status actor { OperationLookup.OperationId = id 80 } CancellationToken.None

        Expect.equal
            status.Outcome
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "Acceptance and own final nonacceptance cannot both become a status answer"

        let! settled =
            Service.resolve actor { OperationLookup.OperationId = id 80 } CancellationToken.None

        Expect.equal
            settled.Outcome
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "Settlement must detect contradiction before replay"

        let! replayed = Service.execute actor input CancellationToken.None

        Expect.equal
            replayed.Outcome
            (ExecuteResult.Failed CoreFault.StoreIntegrityError)
            "Execute must detect contradiction before accepted replay"
    }
