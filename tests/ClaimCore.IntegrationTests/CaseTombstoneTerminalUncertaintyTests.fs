module ClaimCore.IntegrationTests.CaseTombstoneTerminalUncertaintyTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseTombstoneTerminalApprovalFixture
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence

let private ct = CancellationToken.None

let private faultProtocol writer (witness: WitnessProtocol) =
    let path =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Isolated witness capability path is absent")

    use file = WriterCapabilityFile.Load(path)
    let store = file.Use(fun material -> new Store(writer, witness.Identity, material))
    let keyId, _ = store.ReadKeyCheck()
    let material = witnessKey ()

    try
        new WitnessProtocol(
            store,
            new KeyRing(keyId, [ keyId, material ]) :> IKeyCustody,
            witness.Identity,
            fun () -> raise (TimeoutException("Synthetic terminal settlement loss"))
        )
    finally
        CryptographicOperations.ZeroMemory(material)

let private context source (witness: WitnessProtocol) principal caseId =
    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    gate.Tombstone(principal, EndpointAction.ApproveTerminalErasure, caseId, ct)
    |> await
    |> Option.defaultWith (fun () -> failtest "Synthetic steward context is absent")

let private assertUnsettled (fixture: PruneFixture) approvalId =
    use owner = new NpgsqlConnection(fixture.Owner)
    owner.Open()

    use row =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.case_erasure_terminal_approvals WHERE approval_id=@approval",
            owner
        )

    Sql.uuid row "approval" approvalId
    Expect.equal (row.ExecuteScalar() :?> int64) 1L "Primary approval committed once"

    Expect.isSome
        (fixture.Witness.EvidenceStore.TryReadEvidence(approvalId, Intent))
        "Witness intent is retained"

    Expect.isNone
        (fixture.Witness.EvidenceStore.TryReadEvidence(approvalId, SettledAuthority))
        "Lost settlement is not fabricated"

    Expect.throws
        (fun () ->
            DataAudit.runWithSuppression owner fixture.Witness (Some fixture.Commitments) ct
            |> await
            |> ignore)
        "Unsettled approval quarantines full audit"

let private run =
    testCase
        "[CC-ERASE-001] terminal approval lost settlement retries exact without duplicate"
        (fun _ ->
            withPruned (fun fixture (source, _) runtime _ first _ _ ->
                let draft =
                    TombstoneTerminalProposal.ConfirmManagedPayloadAbsence(proposal fixture)

                let copy = TombstoneTerminalProposal.copy draft
                let expiry = copy.ValidUntil.AddMinutes(-1.0)
                let approvalId = Guid.NewGuid()
                use fault = faultProtocol fixture.Writer fixture.Witness
                let storage = PostgresCaseTombstoneStore(source, fault) :> ITombstoneStore

                match
                    storage.ApproveTerminal(
                        context source fault first fixture.CaseId,
                        draft,
                        approvalId,
                        expiry,
                        DateTimeOffset.UtcNow
                    )
                    |> await
                with
                | TombstoneWriteOutcome.Unconfirmed id when id = approvalId -> ()
                | _ -> failtest "Postcommit settlement loss was misclassified."

                assertUnsettled fixture approvalId

                let changed =
                    TombstoneTerminalProposal.ConfirmManagedPayloadAbsence
                        { copy with
                            PolicyId = "synthetic-changed-policy"
                        }

                Expect.equal
                    (approve runtime first changed approvalId expiry)
                    (TombstoneWriteOutcome.Refused
                        ClaimCore.Domain.LifecycleRefusal.ApprovalMismatch)
                    "Changed canonical bytes cannot reconcile same approval ID"

                approve runtime first draft approvalId expiry |> applied approvalId
                approve runtime first draft approvalId expiry |> applied approvalId
                fullAudit fixture))

let tests = testList "terminal erasure uncertainty" [ run ]
