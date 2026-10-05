module ClaimCore.IntegrationTests.InstallationLossRetirementProcessTests

open System.Threading
open System
open System.IO
open System.Text
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.InstallationLossRetirementTests
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private auditFor (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessAuditConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let private privateInputs directory (owner: string) (writer: string) (witness: WitnessProtocol) =
    files directory owner writer witness
    @ [
        "CLAIMCORE_WITNESS_AUDIT_CONNECTION_FILE",
        privateBytes directory "witness-audit.connection" (Encoding.UTF8.GetBytes(auditFor writer))
        "CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE",
        privateBytes
            directory
            "witness-owner.connection"
            (Encoding.UTF8.GetBytes(witnessOwnerFor writer))
    ]

let private invoke command arguments inputs =
    let code, response = runCommand command arguments inputs
    use response = response
    code, response.RootElement.Clone()

[<NoEquality; NoComparison>]
type private DecisionFiles =
    {
        Inputs: (string * string) list
        Candidate: string
        Known: string
        SignatureOne: string
        SignatureTwo: string
    }

let private decisionArguments value known =
    [
        value.Candidate
        "MISSING"
        "MISSING"
        known
        value.SignatureOne
        value.SignatureTwo
    ]

let private requirePrivateDraft code (result: System.Text.Json.JsonElement) (candidate: string) =
    Expect.equal code 0 "Owner process creates the exact private incident draft."

    Expect.equal
        (result.GetProperty("operationOutcome").GetString())
        "COMPLETED"
        "Draft does not mutate the installation."

    Expect.equal
        (File.GetUnixFileMode(candidate))
        (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        "Decision bytes are owner-private."

let private draftPrivate
    directory
    owner
    writer
    witness
    (algorithm: SignatureAlgorithm)
    (firstKey: Key)
    (secondKey: Key)
    (firstId: Guid)
    (secondId: Guid)
    =
    let inputs = privateInputs directory owner writer witness
    let operationId = Guid.NewGuid()

    let known =
        privateBytes
            directory
            "known-operations"
            (Encoding.ASCII.GetBytes(operationId.ToString("D") + "\n"))

    let candidate = Path.Combine(directory, "loss-retirement.candidate")

    let code, result =
        invoke
            "draft-installation-loss-retirement"
            [
                firstId.ToString("D")
                secondId.ToString("D")
                "MISSING"
                "MISSING"
                known
                "KNOWN_OPERATIONS"
                candidate
            ]
            inputs

    requirePrivateDraft code result candidate

    let canonical = File.ReadAllBytes(candidate)

    {
        Inputs = inputs
        Candidate = candidate
        Known = known
        SignatureOne =
            privateBytes directory "first.signature" (algorithm.Sign(firstKey, canonical))
        SignatureTwo =
            privateBytes directory "second.signature" (algorithm.Sign(secondKey, canonical))
    }

let private rejectChangedList directory (witness: WitnessProtocol) decision =
    let altered =
        privateBytes
            directory
            "altered-operations"
            (Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("D") + "\n"))

    let before =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    let code, failure =
        invoke "retire-installation-after-loss" (decisionArguments decision altered) decision.Inputs

    Expect.notEqual code 0 "Changed known-operation bytes are refused."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        before
        "Pre-W0 refusal appends no authority."

    Expect.isFalse
        (failure.GetRawText().Contains(altered, StringComparison.Ordinal))
        "Private operation-list path is not disclosed."

let private settleAndReadBack (witness: WitnessProtocol) decision =
    let arguments = decisionArguments decision decision.Known

    let code, receipt =
        invoke "retire-installation-after-loss" arguments decision.Inputs

    Expect.equal code 0 "Two independently signed owner files retire the installation."

    Expect.equal
        (receipt.GetProperty("operationOutcome").GetString())
        "COMPLETED"
        "Only W0, primary receipt, and W1 justify definite completion."

    let settled = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    Expect.isTrue settled.LossRetired "Old writer authority is terminal."

    let replayCode, replay =
        invoke "reconcile-installation-loss-retirement" arguments decision.Inputs

    Expect.equal replayCode 0 "Exact process retry reads back retirement."

    Expect.equal
        (replay.GetProperty("operationOutcome").GetString())
        "COMPLETED"
        "Reconciliation does not invent a new decision."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        settled.TipSequence
        "Process reconciliation appends no duplicate authority."

let private publishedDecision owner app writer witness =
    setup
        owner
        app
        writer
        witness
        (fun _ _ witness _ algorithm firstKey secondKey firstId secondId writer ->
            let directory = privateRoot ()

            try
                let decision =
                    draftPrivate
                        directory
                        owner
                        writer
                        witness
                        algorithm
                        firstKey
                        secondKey
                        firstId
                        secondId

                rejectChangedList directory witness decision
                settleAndReadBack witness decision
            finally
                Directory.Delete(directory, true))

let tests =
    testList
        "installation loss owner process"
        [
            testCase
                "[CC-BACKUP-001] published owner process binds private loss files and exact W0/W1 reconciliation"
            <| fun _ -> withAuthorityRuntimeDatabase publishedDecision
        ]
