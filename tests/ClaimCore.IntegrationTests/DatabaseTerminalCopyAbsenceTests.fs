module ClaimCore.IntegrationTests.DatabaseTerminalCopyAbsenceTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Database
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.CaseTombstoneTerminalApprovalFixture
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopyInventoryDocuments
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests

let private ct = CancellationToken.None

[<NoEquality; NoComparison>]
type internal SignedAbsence =
    {
        RegistryPath: string
        InspectionPath: string
        KeyPath: string
        ProposalPath: string
        Proposal: TombstoneTerminalProposal
    }

let private signers owner (witness: WitnessProtocol) (runtime: Runtime) proposer =
    let registryHolder = human ("terminal-registry-" + Guid.NewGuid().ToString("N"))
    let verifierHolder = human ("terminal-verifier-" + Guid.NewGuid().ToString("N"))
    grantCustodian runtime proposer registryHolder
    grantCustodian runtime proposer verifierHolder
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let registryKey, algorithm, registryId, _ =
        registeredSigner
            runtime
            proposer
            registryHolder
            CopySignerPurpose.LocationRegistry
            witness
            connection

    let verifierKey, _, verifierId, _ =
        registeredSigner
            runtime
            proposer
            verifierHolder
            CopySignerPurpose.DeletionVerifier
            witness
            connection

    registryKey, verifierKey, algorithm, registryId, verifierId

let internal signedDocuments
    owner
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    (fixture: PruneFixture)
    knownUnmanaged
    =
    let root = privateRoot ()

    let registryKey, verifierKey, algorithm, registryId, verifierId =
        signers owner witness runtime proposer

    use _registry = registryKey
    use _verifier = verifierKey
    let tip = witness.Snapshot()
    let now = DateTimeOffset.UtcNow
    let registry = registryBodyForEntries tip registryId [] knownUnmanaged now

    let inspection =
        inspectionBodyForObservations tip verifierId (digest registry) [] now

    let proposal =
        { proposal fixture with
            CopyInventoryDigest = digest registry
            RelevantCopyCount = 0L
            ExpectedWriterGeneration = tip.WriterGeneration
        }
        |> TombstoneTerminalProposal.ConfirmManagedPayloadAbsence

    let draft = CaseTombstoneTerminalCandidate.proposal proposal

    {
        RegistryPath =
            privateBytes root "terminal-registry.json" (envelope algorithm registryKey registry)
        InspectionPath =
            privateBytes root "terminal-inspection.json" (envelope algorithm verifierKey inspection)
        KeyPath = privateBytes root "terminal-commitment.key" (RandomNumberGenerator.GetBytes(32))
        ProposalPath = privateBytes root "terminal-proposal.json" draft
        Proposal = proposal
    }

let internal approved (runtime: Runtime) first second proposal =
    let expiry = (TombstoneTerminalProposal.copy proposal).ValidUntil.AddMinutes(-1.0)

    for principal in [ first; second ] do
        let id = Guid.NewGuid()
        approve runtime principal proposal id expiry |> applied id

let private execute (fixture: PruneFixture) (signed: SignedAbsence) =
    let directory =
        Path.GetDirectoryName(signed.ProposalPath)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic private directory is absent")

    let settings = files directory fixture.Owner fixture.Writer fixture.Witness

    let prior =
        settings
        |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable(name))

    try
        settings
        |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

        withEnvironment signed.RegistryPath signed.InspectionPath signed.KeyPath (fun () ->
            DatabaseTerminalCopyAbsenceExecution.run fixture.Owner signed.ProposalPath)
    finally
        prior
        |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

let internal phase owner caseId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT phase FROM claimcore.case_erasure_tombstones WHERE case_id=@case",
            connection
        )

    Sql.uuid command "case" caseId

    match command.ExecuteScalar() with
    | :? string as value when not (String.IsNullOrEmpty value) -> value
    | _ -> failtest "Synthetic terminal case phase is unavailable"

let private positive =
    testCase
        "[CC-ERASE-001] owner signed all-ABSENT zero-copy certificate advances pending privacy phase"
        (fun _ ->
            withPruned (fun fixture _ runtime proposer first second _ ->
                let signed =
                    signedDocuments fixture.Owner fixture.Witness runtime proposer fixture []

                approved runtime first second signed.Proposal

                match execute fixture signed with
                | Ok(AdministrationOutcome.Completed None) -> ()
                | _ -> failtest "Signed terminal copy absence did not advance"

                Expect.equal
                    (phase fixture.Owner fixture.CaseId)
                    "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
                    "The witnessed terminal copy event advances only the truthful intermediate phase"

                fullAudit fixture))

let private unmanaged =
    testCase "[CC-ERASE-001] signed known unmanaged copy remains a terminal liability" (fun _ ->
        withPruned (fun fixture _ runtime proposer first second _ ->
            let signed =
                signedDocuments
                    fixture.Owner
                    fixture.Witness
                    runtime
                    proposer
                    fixture
                    [ Guid.NewGuid() ]

            approved runtime first second signed.Proposal

            match execute fixture signed with
            | Ok(AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed) -> ()
            | _ -> failtest "Known unmanaged copy was certified absent"

            Expect.equal
                (phase fixture.Owner fixture.CaseId)
                "ERASURE_PENDING"
                "An unmanaged liability keeps the case pending"))

let private newCopy =
    testCase
        "[CC-ERASE-001] a copy registered after signed inventory blocks terminal certification"
        (fun _ ->
            withPruned (fun fixture _ runtime proposer first second _ ->
                let signed =
                    signedDocuments fixture.Owner fixture.Witness runtime proposer fixture []

                let holder = human ("terminal-later-copy-" + Guid.NewGuid().ToString("N"))
                grantCustodian runtime proposer holder
                use connection = new NpgsqlConnection(fixture.Owner)
                connection.Open()

                let key, algorithm, keyId, _ =
                    registeredSigner
                        runtime
                        proposer
                        holder
                        CopySignerPurpose.CopyAttestor
                        fixture.Witness
                        connection

                use _attestor = key
                let root = privateRoot ()

                let _, _, _, _, _, _ =
                    registerCopy fixture.Owner connection fixture.Witness key algorithm keyId root

                approved runtime first second signed.Proposal

                match execute fixture signed with
                | Ok(AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed) ->
                    ()
                | _ -> failtest "Later managed copy escaped signed inventory"

                Expect.equal
                    (phase fixture.Owner fixture.CaseId)
                    "ERASURE_PENDING"
                    "A later registered copy keeps certification pending"))

let tests = testList "terminal signed copy absence" [ positive; unmanaged; newCopy ]
