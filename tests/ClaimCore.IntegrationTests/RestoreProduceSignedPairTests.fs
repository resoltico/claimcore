module ClaimCore.IntegrationTests.RestoreProduceSignedPairTests

open Expecto
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.RestoreProduceSignedPairFixture

let internal withSignedPair action owner app writer witness =
    RestoreProduceSignedPairFixture.withSignedPair action owner app writer witness

let tests =
    testList
        "signed physical restored pair"
        [
            testCase
                "[CC-BACKUP-001] four verified copies yield a signed synthetic pre-W1 report only"
                (fun _ ->
                    withAuthorityRuntimeDatabase (withSignedPair (fun _ _ _ _ _ _ _ _ _ -> ())))
        ]
