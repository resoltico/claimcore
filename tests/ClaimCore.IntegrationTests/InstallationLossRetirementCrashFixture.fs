module internal ClaimCore.IntegrationTests.InstallationLossRetirementCrashFixture

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.InstallationLossRetirementFixture
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport

[<NoEquality; NoComparison>]
type Decision =
    {
        Value: InstallationLossRetirementDecision
        Canonical: byte array
        FirstSignature: byte array
        SecondSignature: byte array
        KnownSource: byte array
        Commitments: byte array list
        BeforeSequence: int64
    }

let knownSource (operationId: Guid) =
    Encoding.ASCII.GetBytes(operationId.ToString("D") + "\n")

let prepare (context: Context) known mode =
    let before =
        (context.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    let canonical =
        InstallationLossRetirementAdministration.draft
            context.Primary
            context.Witness
            context.Suppression
            context.FirstKey
            context.SecondKey
            None
            None
            known
            mode
        |> await
        |> Option.defaultWith (fun () -> failwith "Synthetic incident draft was refused.")

    let value =
        InstallationLossRetirementCandidate.parse canonical
        |> Option.defaultWith (fun () -> failwith "Synthetic incident draft was invalid.")

    let _, _, commitments =
        InstallationLossOperationCommitments.commitments context.Suppression mode known

    {
        Value = value
        Canonical = canonical
        FirstSignature = context.Algorithm.Sign(context.KeyOne, ReadOnlySpan<byte>(canonical))
        SecondSignature = context.Algorithm.Sign(context.KeyTwo, ReadOnlySpan<byte>(canonical))
        KnownSource = known
        Commitments = commitments
        BeforeSequence = before
    }

let w0 (context: Context) (decision: Decision) =
    InstallationLossRetirementWitness.prepare
        (witnessOwnerFor context.Writer)
        context.Witness
        decision.Value
        decision.Canonical
        decision.FirstSignature
        decision.SecondSignature
        CancellationToken.None
    |> await

let commitPrimary (connection: NpgsqlConnection) (decision: Decision) intent =
    use transaction = connection.BeginTransaction()

    InstallationLossRetirementPrimary.insert
        connection
        transaction
        decision.Value
        decision.Canonical
        decision.FirstSignature
        decision.SecondSignature
        intent
        decision.Commitments

    transaction.Commit()

let reconcile (context: Context) (connection: NpgsqlConnection) (decision: Decision) =
    InstallationLossRetirementAdministration.reconcile
        connection
        (witnessOwnerFor context.Writer)
        context.Witness
        context.Suppression
        decision.Canonical
        decision.FirstSignature
        decision.SecondSignature
        decision.KnownSource
        None
        None
    |> await

let private grant (context: Context) principal role =
    let eventId = Guid.NewGuid()

    (context.Runtime.ForActor principal)
        .Management.SetGrant(
            eventId,
            principal,
            role,
            GrantTarget.Installation,
            true,
            CancellationToken.None
        )
    |> await
    |> appliedManagement eventId

let acceptedArtifact (context: Context) =
    let principal = human "loss-owner-first"
    grant context principal Role.CaseEditor
    grant context principal Role.RecoveryExporter
    grant context principal Role.RecoveryOperator
    let operationId = Guid.NewGuid()
    let reference = "LOSS-" + Guid.NewGuid().ToString("N")
    let request = openRequest operationId reference
    let core = context.Runtime.ForActor principal

    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> ()
    | _ -> failwith "Synthetic accepted operation was not definite."

    let digest =
        request |> RequestRecord.encode |> SHA256.HashData |> Convert.ToHexStringLower

    let artifact =
        match
            core.Recovery.ExportEnvelope(operationId, digest, CancellationToken.None)
            |> await
        with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found value) -> value.Bytes
        | _ -> failwith "Synthetic accepted recovery artifact was unavailable."

    match core.Recovery.PreviewEnvelopeImport(artifact, CancellationToken.None) |> await with
    | RecoveryQueryOutcome.RecoverySucceeded _ -> ()
    | _ -> failwith "Synthetic old artifact was not usable before retirement."

    operationId, reference, artifact
