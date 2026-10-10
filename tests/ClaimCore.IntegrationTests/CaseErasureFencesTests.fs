module ClaimCore.IntegrationTests.CaseErasureFencesTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport

let private caseId owner reference =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT case_id FROM claimcore.cases WHERE case_reference=@reference",
            connection
        )

    Sql.text command "reference" reference
    command.ExecuteScalar() :?> Guid

let private openedCase actor =
    let input =
        openRequest (Guid.NewGuid()) ("ERASURE-FENCE-" + Guid.NewGuid().ToString("N"))

    executeAccepted actor input
    input

let private requestErasure actor (input: CommandRequest) =
    let current = review actor input.CaseReference

    let request =
        change
            (Guid.NewGuid())
            input.CaseReference
            current
            (LifecycleMutation.RequestErasure "Synthetic erasure fence")

    actor.Lifecycle.Apply(request, CancellationToken.None) |> await |> ignore

let private grantFence =
    testCase "[CC-ERASE-001] erasure fence rejects new case grants but permits revocation" (fun _ ->
        CaseLifecycleStoreFixture.setup
            (fun owner (source, _) witness runtime proposer first second _ _ ->
                let actor = runtime.ForActor proposer
                let input = openedCase actor
                let id = caseId owner input.CaseReference
                let registry = ActorGrantRegistry(source, witness)
                let grants = source

                let grant =
                    {
                        Role = Role.CaseReader
                        Scope = GrantScope.Case id
                    }

                let firstId = actorId grants first
                let secondId = actorId grants second
                registry.SetGrant(proposer, firstId, grant, true) |> await |> applied
                requestErasure actor input

                let before =
                    (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())
                        .TipSequence

                match registry.SetGrant(proposer, secondId, grant, true) |> await with
                | AuthorityWriteOutcome.Refused -> ()
                | _ -> failtest "New case grant crossed the erasure fence"

                Expect.equal
                    ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())
                        .TipSequence)
                    before
                    "Refused grant has no intent"

                registry.SetGrant(proposer, firstId, grant, false) |> await |> applied))

let private copyFence =
    testCase
        "[CC-ERASE-001] case-linked copy registration refuses a witnessed erasure fence"
        (fun _ ->
            CaseLifecycleStoreFixture.setup (fun owner _ witness runtime proposer _ _ _ _ ->
                let actor = runtime.ForActor proposer
                let input = openedCase actor
                requestErasure actor input
                let id = caseId owner input.CaseReference
                let custodian = human "copy-fence-custodian"
                grantCustodian runtime proposer custodian
                use connection = new NpgsqlConnection(owner)
                connection.Open()

                let key, algorithm, keyId, _ =
                    registeredSigner
                        runtime
                        proposer
                        custodian
                        CopySignerPurpose.CopyAttestor
                        witness
                        connection

                use key = key
                let eventId = Guid.NewGuid()

                let canonical =
                    registerVariant
                        owner
                        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()))
                        keyId
                        eventId
                        (Guid.NewGuid())
                        "EXPORT"
                        "NONE"
                        (Some id)

                let signature = algorithm.Sign(key, canonical)

                let before =
                    (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())
                        .TipSequence

                match
                    ManagedCopyAdministration.ingest connection witness canonical signature
                    |> await
                with
                | AuthorityWriteOutcome.Refused -> ()
                | _ -> failtest "New case-linked copy crossed the erasure fence"

                Expect.equal
                    ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())
                        .TipSequence)
                    before
                    "No copy intent was appended"))

let tests = testList "case erasure authority fences" [ grantFence; copyFence ]
