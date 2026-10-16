module ClaimCore.IntegrationTests.StoreTests

open Expecto

[<Tests>]
let tests = ClaimCore.TestSupport.StoreContractTests.tests PostgresFixture.fixture
