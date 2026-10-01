namespace ClaimCore.Contracts

open System
open ClaimCore.Application
open HttpInputSupport

module HttpRealDataActivationInput =
    let review bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "planId" ]
            required "planId" values |> stringValue |> operationIdValue)

    let approve bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties
                    [
                        "approvalId"
                        "planId"
                        "activationId"
                        "installationId"
                        "lineageId"
                        "epoch"
                        "writerGeneration"
                        "activationPlanSha256"
                        "policySha256"
                        "reviewWitnessSequence"
                        "reviewWitnessHash"
                        "expectedWitnessSequence"
                        "expectedWitnessHash"
                        "expiresAt"
                    ]

            let uuid key =
                required key values |> stringValue |> operationIdValue

            let number key =
                required key values |> stringValue |> canonicalNonNegativeInt64

            let digest key =
                required key values |> stringValue |> digestValue |> Convert.FromHexString

            {
                ApprovalId = uuid "approvalId"
                PlanId = uuid "planId"
                ActivationId = uuid "activationId"
                InstallationId = uuid "installationId"
                LineageId = uuid "lineageId"
                Epoch = number "epoch"
                WriterGeneration = number "writerGeneration"
                ActivationPlanSha256 = digest "activationPlanSha256"
                PolicySha256 = digest "policySha256"
                ReviewWitnessSequence = number "reviewWitnessSequence"
                ReviewWitnessHash = digest "reviewWitnessHash"
                ExpectedWitnessSequence = number "expectedWitnessSequence"
                ExpectedWitnessHash = digest "expectedWitnessHash"
                ExpiresAt =
                    required "expiresAt" values |> stringValue |> utcMicrosecondTimestampValue
            })
