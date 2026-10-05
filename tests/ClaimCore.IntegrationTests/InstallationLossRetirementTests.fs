module ClaimCore.IntegrationTests.InstallationLossRetirementTests

open System
open System.Text
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.InstallationLossRetirementFixture
open ClaimCore.IntegrationTests.InstallationLossRetirementDenialAudit

// Keep this exact shared setup seam for the published owner-process test.
let internal witnessOwnerFor writer =
    InstallationLossRetirementFixture.witnessOwnerFor writer

let internal setup owner app writer (witness: WitnessProtocol) test =
    InstallationLossRetirementFixture.run owner app writer witness (fun context ->
        test
            context.Runtime
            context.Primary
            context.Witness
            context.Suppression
            context.Algorithm
            context.KeyOne
            context.KeyTwo
            context.FirstKey
            context.SecondKey
            context.Writer)

let private knownFile (operationId: Guid) =
    Encoding.ASCII.GetBytes(operationId.ToString("D") + "\n")

let private signed (context: Context) (canonical: byte array) =
    context.Algorithm.Sign(context.KeyOne, ReadOnlySpan<byte>(canonical)),
    context.Algorithm.Sign(context.KeyTwo, ReadOnlySpan<byte>(canonical))

let private accepted (context: Context) canonical firstSignature secondSignature known before =
    let decision =
        InstallationLossRetirementCandidate.parse canonical
        |> Option.defaultWith (fun () -> failtest "Loss candidate is invalid.")

    let result =
        InstallationLossRetirementAdministration.record
            context.Primary
            (witnessOwnerFor context.Writer)
            context.Witness
            context.Suppression
            canonical
            firstSignature
            secondSignature
            known
            None
            None
        |> await

    match result with
    | InstallationLossRetirementOutcome.Retired(id, sequence, _) ->
        Expect.equal id decision.RetirementId "Exact terminal identity retained."
        Expect.equal sequence (before + 2L) "W0 and W1 are ordered."
    | _ -> failtest "Two-owner terminal retirement did not settle."

    decision

let private exactReplay (context: Context) canonical firstSignature secondSignature known tip =
    let replay =
        InstallationLossRetirementAdministration.reconcile
            context.Primary
            (witnessOwnerFor context.Writer)
            context.Witness
            context.Suppression
            canonical
            firstSignature
            secondSignature
            known
            None
            None
        |> await

    match replay with
    | InstallationLossRetirementOutcome.Retired _ -> ()
    | _ -> failtest "Exact retirement reconciliation changed identity."

    Expect.equal
        ((context.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        tip.TipSequence
        "Exact retry appends no authority event."

let private fencedAndAudited (context: Context) tip =
    Expect.isTrue tip.LossRetired "Witness terminal flag is irreversible."
    Expect.isFalse tip.LossRetirementPending "W1 settled the pending fence."

    Expect.throwsT<InvalidOperationException>
        (fun () -> (context.Witness.Admit(CancellationToken.None).GetAwaiter().GetResult()))
        "Old writer cannot readmit after W1."

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            (context.Runtime.ForActor(human "loss-owner-first")).Definition(CancellationToken.None)
            |> await
            |> ignore)
        "Already-open runtime cannot disclose case work after retirement."

    use audit = new NpgsqlConnection(context.OwnerConnectionString)
    audit.Open()
    let summary = DataAudit.run audit context.Witness CancellationToken.None |> await
    Expect.equal summary.PendingIntents 0L "Settled terminal pair is auditable."
    Expect.equal summary.WitnessCutoff tip.TipSequence "Audit includes W1."

let private knownDenial (context: Context) retirementId =
    use command =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.installation_loss_operation_denials "
            + "WHERE retirement_id=@retirement",
            context.Primary
        )

    command.Parameters.AddWithValue("retirement", retirementId) |> ignore

    Expect.equal
        (command.ExecuteScalar() :?> int64)
        1L
        "Known identity is retained only as a keyed commitment."

    verifyTamper context retirementId

let private terminalCase (context: Context) =
    let known = knownFile (Guid.NewGuid())
    let canonical = candidate context known InstallationLossOperationSet.Known
    let firstSignature, secondSignature = signed context canonical

    let before =
        (context.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    let decision =
        accepted context canonical firstSignature secondSignature known before

    let tip =
        (context.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    exactReplay context canonical firstSignature secondSignature known tip
    fencedAndAudited context tip
    knownDenial context decision.RetirementId

let private refusedBeforeW0
    (context: Context)
    canonical
    firstSignature
    secondSignature
    known
    before
    =
    let result =
        InstallationLossRetirementAdministration.record
            context.Primary
            (witnessOwnerFor context.Writer)
            context.Witness
            context.Suppression
            canonical
            firstSignature
            secondSignature
            known
            None
            None
        |> await

    match result with
    | InstallationLossRetirementOutcome.Refused -> ()
    | _ -> failtest "Invalid signed incident input was not a pre-W0 refusal."

    Expect.equal
        ((context.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        before
        "Invalid signed input cannot append a witness intent."

let private alteredCase (context: Context) =
    let original = knownFile (Guid.NewGuid())
    let altered = knownFile (Guid.NewGuid())
    let canonical = candidate context original InstallationLossOperationSet.Known
    let firstSignature, secondSignature = signed context canonical

    let before =
        (context.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    refusedBeforeW0 context canonical firstSignature secondSignature altered before
    refusedBeforeW0 context canonical secondSignature firstSignature original before

let private pendingUnknown (context: Context) canonical firstSignature secondSignature =
    let decision =
        InstallationLossRetirementCandidate.parse canonical
        |> Option.defaultWith (fun () -> failtest "Unknown-set candidate is invalid.")

    InstallationLossRetirementWitness.prepare
        (witnessOwnerFor context.Writer)
        context.Witness
        decision
        canonical
        firstSignature
        secondSignature
        CancellationToken.None
    |> await
    |> ignore

    let pending =
        (context.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    Expect.isTrue pending.LossRetirementPending "W0 alone closes the old writer."
    Expect.isFalse pending.LossRetired "W0 is not a settled receipt."

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            (context.Runtime.ForActor(human "loss-owner-first")).Definition(CancellationToken.None)
            |> await
            |> ignore)
        "Already-open runtime is fenced immediately after W0."

    use audit = new NpgsqlConnection(context.OwnerConnectionString)
    audit.Open()
    let observed = DataAudit.run audit context.Witness CancellationToken.None |> await
    Expect.equal observed.PendingIntents 1L "W0 is pending, not falsely settled."
    decision

let private settleUnknown
    (context: Context)
    canonical
    firstSignature
    secondSignature
    (decision: InstallationLossRetirementDecision)
    =
    let result =
        InstallationLossRetirementAdministration.reconcile
            context.Primary
            (witnessOwnerFor context.Writer)
            context.Witness
            context.Suppression
            canonical
            firstSignature
            secondSignature
            Array.empty
            None
            None
        |> await

    match result with
    | InstallationLossRetirementOutcome.Retired(id, _, _) ->
        Expect.equal id decision.RetirementId "Original loss identity is reconciled."
    | _ -> failtest "Exact W0 retry did not settle terminal decision."

    use denied =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.installation_loss_operation_denials",
            context.Primary
        )

    Expect.equal
        (denied.ExecuteScalar() :?> int64)
        0L
        "Unknown set uses the whole-installation fence, not guessed IDs."

let private unknownCase (context: Context) =
    let canonical = candidate context Array.empty InstallationLossOperationSet.Unknown
    let firstSignature, secondSignature = signed context canonical
    let decision = pendingUnknown context canonical firstSignature secondSignature
    settleUnknown context canonical firstSignature secondSignature decision

let tests =
    testList
        "installation loss retirement"
        [
            testCase
                "[CC-BACKUP-001] two owners permanently fence an old installation with known-operation denial"
                (fun _ ->
                    withAuthorityRuntimeDatabase (fun owner app writer witness ->
                        InstallationLossRetirementFixture.run
                            owner
                            app
                            writer
                            witness
                            terminalCase))
            testCase "[CC-BACKUP-001] changed private denial list refuses before W0" (fun _ ->
                withAuthorityRuntimeDatabase (fun owner app writer witness ->
                    InstallationLossRetirementFixture.run owner app writer witness alteredCase))
            testCase
                "[CC-BACKUP-001] missing-evidence unknown-set W0 stays pending until reconciliation"
                (fun _ ->
                    withAuthorityRuntimeDatabase (fun owner app writer witness ->
                        InstallationLossRetirementFixture.run owner app writer witness unknownCase))
        ]
