module ClaimCore.IntegrationTests.WriterHandoffProtocolTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.WriterHandoffApprovalTests
open ClaimCore.IntegrationTests.WriterHandoffDocuments
open ClaimCore.IntegrationTests.WriterHandoffPrimaryFixture
open ClaimCore.IntegrationTests.WriterHandoffProcessFixture
open ClaimCore.IntegrationTests.WriterHandoffSyntheticVerifier
open ClaimCore.IntegrationTests.FixturePrivateFiles
open ClaimCore.IntegrationTests.WriterHandoffProtocolAssertions
open ClaimCore.IntegrationTests.WriterHandoffProtocolPreparation
open ClaimCore.IntegrationTests.WriterHandoffProtocolCommit

let private withCapabilities action =
    let capabilityPath =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic writer capability path is absent.")

    let oldCapability = File.ReadAllBytes(capabilityPath)
    let newCapability = RandomNumberGenerator.GetBytes(32)

    try
        action oldCapability newCapability
    finally
        CryptographicOperations.ZeroMemory(oldCapability)
        CryptographicOperations.ZeroMemory(newCapability)

let private handoff simulateCrash owner app writer (witness: WitnessProtocol) =
    let first = human "writer-handoff-owner-one"
    let second = human "writer-handoff-owner-two"
    let holder = human "writer-handoff-checkpoint-holder"
    provision owner witness first |> applied
    use runtime = openRuntime app writer
    secondOwner runtime first second
    grantCustodian runtime first holder
    use primary = new NpgsqlConnection(owner)
    primary.Open()

    let key, algorithm, keyId, _ =
        registeredSigner runtime first holder CopySignerPurpose.Checkpoint witness primary

    use key = key

    withCapabilities (fun oldCapability newCapability ->
        let context =
            prepareHandoff
                runtime
                witness
                primary
                owner
                app
                writer
                first
                second
                keyId
                key
                algorithm
                oldCapability
                newCapability

        commitHandoff
            context
            primary
            witness
            runtime
            owner
            app
            writer
            first
            key
            algorithm
            simulateCrash
            oldCapability
            newCapability)

let private postCommitDisclosureFence () =
    let mutable settled = false

    use admission =
        new RuntimeAdmission(
            { new IDisposable with
                member _.Dispose() = ()
            },
            TimeSpan.FromSeconds 1.,
            (fun () -> ()),
            (fun () ->
                if settled then
                    invalidOp "Synthetic handoff became pending."

                { new IDisposable with
                    member _.Dispose() = ()
                }),
            {
                RequireCaseMutation = (fun () -> ())
                RequireCaseRead = (fun () -> ())
                RequireAuthoritySetup = (fun () -> ())
                RequireAuthorityRead = (fun () -> ())
                CommitHealth =
                    { new ICaseMutationCommitHealth with
                        member _.VerifyLocked(_, _) = ()
                    }
                CommitHealthRequired = false
            }
        )

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            admission.Run(fun () ->
                task {
                    settled <- true
                    return "claimant-bearing receipt"
                })
            |> await
            |> ignore)
        "A handoff between mutation settlement and disclosure withholds the outcome."

    Expect.isTrue settled "The mutation completed before its disclosure fence refused."

let tests =
    testList
        "writer handoff protocol"
        [
            testCase
                "[CC-BACKUP-001] witnessed handoff fences old runtime and audits exact two-cluster cutoff"
                (fun _ -> withAuthorityRuntimeDatabase (handoff false))
            testCase
                "[CC-BACKUP-001] owner reconciles witnessed COMMIT after lost primary response without a second settlement"
                (fun _ -> withAuthorityRuntimeDatabase (handoff true))
            testCase
                "[CC-BACKUP-001] postcommit handoff fence withholds claimant-bearing mutation outcome"
                (fun _ -> postCommitDisclosureFence ())
        ]
