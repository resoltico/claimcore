module internal ClaimCore.IntegrationTests.CaseLifecycleStoreFixture

open System.Threading
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private grant (registry: ActorGrantRegistry) owner source principal role =
    registry.SetGrant(
        owner,
        actorId (new ActorGrantStore(source)) principal,
        {
            Role = role
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> applied

let setup action =
    withAuthorityRuntimeDatabase (fun owner app writer witness ->
        let proposer = human "lifecycle-proposer"
        let firstApprover = human "lifecycle-approver-one"
        let secondApprover = human "lifecycle-approver-two"
        let ungranted = human "lifecycle-ungranted"

        provision owner witness proposer |> applied
        use source = RuntimeDataSource.create app
        let registry = new ActorGrantRegistry(source, witness)

        for principal in [ firstApprover; secondApprover; ungranted ] do
            registry.RegisterActor(proposer, principal) |> await |> applied

        grant registry proposer source proposer Role.CaseEditor

        for principal in [ proposer; firstApprover; secondApprover ] do
            grant registry proposer source principal Role.DataSteward

        use runtime =
            Runtime.OpenPostgres(
                app,
                writer,
                witnessKey (),
                suppressionKeyFile (),
                artifactKeyRingFile (),
                CancellationToken.None
            )
            |> await
            |> accepted

        action
            owner
            (source, app)
            witness
            runtime
            proposer
            firstApprover
            secondApprover
            ungranted
            writer)
