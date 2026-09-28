module internal ClaimCore.IntegrationTests.ManagedCopyAdoptedDeletionProcess

open System
open Expecto
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests
open ClaimCore.IntegrationTests.ManagedCopyAdoptedAbsenceCheck

let execute
    directory
    owner
    writer
    (witness: WitnessProtocol)
    registryPath
    reportPath
    keyPath
    canonical
    signature
    =
    require owner witness registryPath reportPath keyPath canonical
    let canonicalPath = privateBytes directory "adopted-deletion.json" canonical
    let signaturePath = privateBytes directory "adopted-deletion.sig" signature

    let inputs =
        files directory owner writer witness
        @ [
            "CLAIMCORE_COPY_LOCATION_REGISTRY_FILE", registryPath
            "CLAIMCORE_COPY_LOCATION_INSPECTION_FILE", reportPath
            "CLAIMCORE_COPY_COMMITMENT_KEY_FILE", keyPath
        ]

    let mutable acceptedTip = 0L

    for pass in 1..2 do
        let code, result =
            runCommand "verify-delete-adopted-copy" [ canonicalPath; signaturePath ] inputs

        use result = result
        Expect.equal code 0 "Owner process verified adopted-copy deletion"

        Expect.equal
            (result.RootElement.GetProperty("operationOutcome").GetString())
            "COMPLETED"
            "Owner process returns exact completed classification"

        Expect.isFalse
            (result.RootElement.GetRawText().Contains(directory, StringComparison.Ordinal))
            "Private inventory path stays out of response"

        if pass = 1 then
            acceptedTip <- witness.Snapshot().TipSequence
        else
            Expect.equal
                (witness.Snapshot().TipSequence)
                acceptedTip
                "Exact retry adds no witness event after approval and deletion"
