module ClaimCore.IntegrationTests.ManagedCopyKindTests

open System
open System.Threading
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport

let private verifyKind
    owner
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (key: Key)
    (algorithm: SignatureAlgorithm)
    keyId
    caseId
    (kind, cluster, scoped)
    =
    let eventId = Guid.NewGuid()
    let copyId = Guid.NewGuid()
    let source = if scoped then Some caseId else None

    let canonical =
        registerVariant
            owner
            ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()))
            keyId
            eventId
            copyId
            kind
            cluster
            source

    let signature = algorithm.Sign(key, canonical)
    Expect.isSome (ManagedCopyRegistrationAttestation.parse canonical) "Exact kind shape is valid"

    let wrongSource = if scoped then None else Some caseId

    let wrong =
        registerVariant
            owner
            ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()))
            keyId
            (Guid.NewGuid())
            (Guid.NewGuid())
            kind
            cluster
            wrongSource

    Expect.isNone (ManagedCopyRegistrationAttestation.parse wrong) "Wrong case scope is refused"

    ManagedCopyAdministration.ingest connection witness canonical signature
    |> await
    |> acceptedCopy eventId

    for phase in [ Intent; SettledAuthority ] do
        let evidence =
            (witness.EvidenceStore
                .TryReadEvidence(eventId, phase, CancellationToken.None)
                .GetAwaiter()
                .GetResult())
            |> Option.defaultWith (fun () -> failtest "Signed copy witness phase is absent.")

        Expect.equal
            evidence.Ticket.SubjectCaseId
            source
            "Witness phase inherits exact copy subject"

        Expect.equal
            evidence.Ticket.ScopeKind
            (if scoped then Case else Installation)
            "Case copies have case-scoped witness ciphertext"

    Expect.isTrue
        (ManagedCopyOwnerInspection.inspect connection witness copyId |> await)
        "Owner inspection retains signed case scope and witness proof"

let private nonPostgresKinds =
    testCase
        "[CC-BACKUP-001] signed non-backup managed-copy kinds retain exact case scope"
        (fun _ ->
            withAuthorityRuntimeDatabase (fun owner app writer witness ->
                let principal = human "copy-kind-owner"
                let custodian = human "copy-kind-custodian"
                provision owner witness principal |> ActorGrantTestSupport.applied
                use runtime = openRuntime app writer
                grantCustodian runtime principal custodian
                use connection = new NpgsqlConnection(owner)
                connection.Open()

                let key, algorithm, keyId, _ =
                    registeredSigner
                        runtime
                        principal
                        custodian
                        CopySignerPurpose.CopyAttestor
                        witness
                        connection

                use key = key
                let caseId = Guid.NewGuid()

                for variant in
                    [
                        "WITNESS_PAYLOAD", "WITNESS", true
                        "SNAPSHOT", "PRIMARY", false
                        "REPLICA", "WITNESS", false
                        "EXPORT", "NONE", true
                        "ENCRYPTION_KEY_COPY", "NONE", false
                    ] do
                    verifyKind owner connection witness key algorithm keyId caseId variant

                use source = RuntimeDataSource.create app
                use audit = RuntimeDatabase.openConnection source
                let verified = DataAudit.run audit witness CancellationToken.None |> await

                Expect.equal
                    verified.OwnerManagedCopies
                    5L
                    "Every signed kind is in the full audit."))

let tests = testList "managed-copy owner ingest" [ nonPostgresKinds ]
