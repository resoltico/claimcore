module internal ClaimCore.IntegrationTests.InstallationLossRetirementFixture

open System
open System.Threading
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.FixturePrivateFiles
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyIngestTests

[<NoEquality; NoComparison>]
type Context =
    {
        Runtime: Runtime
        Primary: NpgsqlConnection
        OwnerConnectionString: string
        Witness: WitnessProtocol
        Suppression: ISuppressionCommitments
        Algorithm: SignatureAlgorithm
        KeyOne: Key
        KeyTwo: Key
        FirstKey: Guid
        SecondKey: Guid
        Writer: string
    }

let witnessOwnerFor (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let private grant (runtime: Runtime) owner holder role =
    let eventId = Guid.NewGuid()

    (runtime.ForActor owner)
        .Management.SetGrant(
            eventId,
            holder,
            role,
            GrantTarget.Installation,
            true,
            CancellationToken.None
        )
    |> await
    |> appliedManagement eventId

let private owners (runtime: Runtime) first second =
    let registerId = Guid.NewGuid()

    (runtime.ForActor first).Management.RegisterActor(registerId, second, CancellationToken.None)
    |> await
    |> appliedManagement registerId

    grant runtime first second Role.Owner
    grant runtime first second Role.AuditorCustodian
    grant runtime second first Role.AuditorCustodian

let run owner app writer (witness: WitnessProtocol) action =
    let first = human "loss-owner-first"
    let second = human "loss-owner-second"
    provision owner witness first |> applied
    use runtime = openRuntime app writer
    owners runtime first second
    use primary = new NpgsqlConnection(owner)
    primary.Open()

    let keyOne, algorithm, firstKey, _ =
        registeredSigner
            runtime
            second
            first
            CopySignerPurpose.InstallationLossRetirement
            witness
            primary

    let keyTwo, _, secondKey, _ =
        registeredSigner
            runtime
            first
            second
            CopySignerPurpose.InstallationLossRetirement
            witness
            primary

    use keyOne = keyOne
    use keyTwo = keyTwo

    action
        {
            Runtime = runtime
            Primary = primary
            OwnerConnectionString = owner
            Witness = witness
            Suppression = syntheticCommitments witness.Identity
            Algorithm = algorithm
            KeyOne = keyOne
            KeyTwo = keyTwo
            FirstKey = firstKey
            SecondKey = secondKey
            Writer = writer
        }
