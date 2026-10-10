module internal ClaimCore.IntegrationTests.RestorePhysicalCopyFixture

open System
open System.IO
open System.Security.Cryptography
open System.Text.RegularExpressions
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.RestorePhysicalProcess
open ClaimCore.TestSupport

let private acceptedCase owner app writer witness =
    let principal = human "physical-restore-owner"
    provision owner witness principal |> applied
    use source = RuntimeDataSource.create app
    let registry = new ActorGrantRegistry(source, witness)
    let grants = source

    registry.SetGrant(
        principal,
        actorId grants principal,
        {
            Role = Role.CaseEditor
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> applied

    let opening =
        Runtime.OpenPostgres(
            app,
            writer,
            witnessKey (),
            suppressionKeyFile (),
            artifactKeyRingFile (),
            CancellationToken.None
        )
        |> await

    use runtime =
        match opening with
        | Ok value -> value
        | Error RuntimeOpenFault.RuntimeConfigurationInvalid ->
            failtest "Synthetic physical capture runtime configuration was invalid."
        | Error RuntimeOpenFault.RuntimeSchemaMismatch ->
            failtest "Synthetic physical capture runtime schema mismatched."
        | Error RuntimeOpenFault.RuntimeStoreUnavailable ->
            failtest "Synthetic physical capture runtime store was unavailable."
        | Error RuntimeOpenFault.RuntimeStoreIntegrityError ->
            failtest "Synthetic physical capture runtime integrity was refused."
        | Error RuntimeOpenFault.RuntimeCancelled ->
            failtest "Synthetic physical capture runtime opening was cancelled."

    let request = newRequest ()

    match (runtime.ForActor principal).Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> ()
    | _ -> failtest "Synthetic case operation was not witnessed and accepted"

let private startRestores scratch =
    let primaryId, witnessId = containerIds ()

    let primaryRole =
        NpgsqlConnectionStringBuilder(adminConnection ()).Username
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic primary owner role is missing")

    let witnessRole =
        NpgsqlConnectionStringBuilder(witnessOwnerConnection ()).Username
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic witness owner role is missing")

    let script =
        Path.Combine(RepositoryRoot.find (), "eng/backup/Restore-IsolatedTestPair.sh")

    let code, output, stage =
        run "bash" [ script; primaryId; witnessId; primaryRole; witnessRole; scratch ]

    if code <> 0 then
        failtest ("Isolated physical restore failed at " + stage)

    let parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries)

    if
        parts.Length <> 4
        || not (Regex.IsMatch(parts[0], "^[0-9a-f]{64}$"))
        || not (Regex.IsMatch(parts[2], "^[0-9a-f]{64}$"))
    then
        failtest "Isolated restore returned invalid container identities"

    parts[0], Int32.Parse(parts[1]), parts[2], Int32.Parse(parts[3])

let internal auditRestored owner app writer (witness: WitnessProtocol) primaryPort witnessPort =
    let access = restoredAccess owner app writer primaryPort witnessPort

    let keyId = witness.KeyCustody.ActiveKeyId
    let key = witnessKey ()

    try
        use custody = new KeyRing(keyId, [ keyId, key ]) :> IKeyCustody
        use suppression = SuppressionKeyFile.Load(suppressionKeyFile ())

        let summary, tip, facts =
            DatabaseVerifyData.auditedRestoredWith
                access.Owner
                access.WitnessAudit
                custody
                suppression
                (fun connection transaction protocol audit snapshot ->
                    DatabaseRestoreLive.inspect
                        connection
                        transaction
                        access.WitnessOwner
                        protocol
                        audit
                        snapshot)
            |> await

        Expect.equal
            summary.PendingIntents
            0L
            "Restored physical pair has no pending witness intent"

        Expect.equal facts.WitnessCutoff tip.TipSequence "Restored physical cutoff is exact"
        facts
    finally
        CryptographicOperations.ZeroMemory(key)

let private signer scratch =
    let root = Path.Combine(scratch, "registered-proof")

    let mode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    Directory.CreateDirectory(root, mode) |> ignore
    let privateKey = Path.Combine(root, "verifier.pem")
    let publicDer = Path.Combine(root, "verifier.der")

    let code, _, _ =
        run "openssl" [ "genpkey"; "-algorithm"; "Ed25519"; "-out"; privateKey ]

    if code <> 0 then
        failtest "Isolated verifier key generation failed"

    let code, _, _ =
        run "openssl" [ "pkey"; "-in"; privateKey; "-pubout"; "-outform"; "DER"; "-out"; publicDer ]

    if code <> 0 then
        failtest "Isolated verifier public key generation failed"

    File.SetUnixFileMode(privateKey, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    let der = File.ReadAllBytes(publicDer)
    let prefix = Convert.FromHexString("302a300506032b6570032100")

    if der.Length <> 44 || der.AsSpan(0, 12).ToArray() <> prefix then
        failtest "Isolated Ed25519 public key encoding is unexpected"

    privateKey, der[12..]

let private describe
    (scratch: string)
    (owner: string)
    (writer: string)
    (facts: RestoredPairFacts)
    captureTip
    sourcePrimary
    sourceWitness
    =
    let connection = NpgsqlConnectionStringBuilder(owner)

    let role =
        connection.Username
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic owner role missing")

    let database =
        connection.Database
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic database missing")

    let witnessConnection = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())

    let witnessRole =
        witnessConnection.Username
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic witness owner role missing")

    let witnessDatabase =
        NpgsqlConnectionStringBuilder(writer).Database
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic witness database missing")

    let keyPath, publicKey = signer scratch

    RestorePhysicalArchiveEvidence.capture
        scratch
        role
        database
        witnessRole
        witnessDatabase
        keyPath
        publicKey
        facts
        captureTip
        sourcePrimary
        sourceWitness

let private describedCapture
    scratch
    owner
    app
    writer
    (witness: WitnessProtocol)
    (captured: Snapshot)
    sourcePrimary
    sourceWitness
    primaryPort
    witnessPort
    =
    let afterCapture =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    Expect.equal afterCapture.TipSequence captured.TipSequence "Physical capture cutoff moved"
    Expect.equal afterCapture.TipHash captured.TipHash "Physical capture hash moved"
    let facts = auditRestored owner app writer witness primaryPort witnessPort
    Expect.equal facts.WitnessCutoff captured.TipSequence "Restored audit cutoff moved"
    let access = restoredAccess owner app writer primaryPort witnessPort
    RestorePhysicalIdentityChecks.requireWitnessIdentity access facts captured

    let described =
        describe scratch owner writer facts captured sourcePrimary sourceWitness

    Expect.equal
        described.WitnessDatabaseName
        (NpgsqlConnectionStringBuilder(access.WitnessOwner).Database
         |> Option.ofObj
         |> Option.defaultWith (fun () -> failtest "Restored witness database is absent"))
        "Physical verifier targets the audited restored witness database"

    described

let withCapturedPrimary callback owner app writer (witness: WitnessProtocol) =
    acceptedCase owner app writer witness

    let captured = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    let scratch = privateScratch ()
    let sourcePrimary, sourceWitness = containerIds ()
    let mutable primaryRestore = ""
    let mutable witnessRestore = ""

    try
        let primaryId, primaryPort, witnessId, witnessPort = startRestores scratch
        primaryRestore <- primaryId
        witnessRestore <- witnessId

        describedCapture
            scratch
            owner
            app
            writer
            witness
            captured
            sourcePrimary
            sourceWitness
            primaryPort
            witnessPort
        |> callback
    finally
        for id in [ witnessRestore; primaryRestore ] do
            if id <> "" then
                run "docker" [ "stop"; id ] |> ignore

        Directory.Delete(scratch, true)
