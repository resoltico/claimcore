module ClaimCore.IntegrationTests.WriterHandoffAbortProtocolTests

open Expecto
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.WriterHandoffAbortScenario
open ClaimCore.IntegrationTests.WriterHandoffAbortStages

let private run owner app writer witness =
    withScenario owner app writer witness (fun scenario ->
        use source = RuntimeDataSource.create app
        let evidence = prepare scenario source
        retainPending evidence
        commitPrimary evidence
        releasePair evidence
        assertRestored evidence)

let tests =
    testList
        "writer handoff abort"
        [
            testCase
                "[CC-BACKUP-001] two owner-held signatures abort pending handoff in three quarantined steps"
                (fun _ -> withAuthorityRuntimeDatabase run)
        ]
