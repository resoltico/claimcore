module internal ClaimCore.IntegrationTests.WriterHandoffProcessFixture

open System
open System.IO
open Expecto
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture

let verifyData
    owner
    writer
    (witness: WitnessProtocol)
    expectedStatus
    preparations
    handoffs
    pending
    =
    let directory = privateRoot ()

    try
        let inputs = files directory owner writer witness
        let code, response = runCommand "verify-data" [] inputs
        use response = response
        Expect.equal code 0 "Owner verify-data emits a typed audit report."
        let root = response.RootElement
        Expect.equal (root.GetProperty("status").GetString()) expectedStatus "Exact audit status"
        let counts = root.GetProperty("counts")

        Expect.equal
            (counts.GetProperty("writerHandoffPreparations").GetString())
            preparations
            "Exact witnessed PREPARE count"

        Expect.equal
            (counts.GetProperty("writerHandoffs").GetString())
            handoffs
            "Exact completed handoff count"

        Expect.equal
            (counts.GetProperty("writerActivations").GetString())
            "0"
            "W1 alone does not activate the restored writer"

        Expect.equal
            (counts.GetProperty("pendingIntents").GetString())
            pending
            "Unsettled intent remains explicit"
    finally
        Directory.Delete(directory, true)
