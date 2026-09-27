namespace ClaimCore.Web

open System
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts
open HttpInputSupport

module internal HttpSignerApprovalInput =
    let private action =
        function
        | "REGISTER" -> CopySignerAction.Register
        | "RETIRE" -> CopySignerAction.Retire
        | _ -> fail HttpInputProblem.InvalidJson

    let private purpose =
        function
        | "COPY_ATTESTOR" -> CopySignerPurpose.CopyAttestor
        | "LOCATION_REGISTRY" -> CopySignerPurpose.LocationRegistry
        | "LOCATION_INSPECTOR" -> CopySignerPurpose.LocationInspector
        | "DELETION_VERIFIER" -> CopySignerPurpose.DeletionVerifier
        | "RESTORE_COPY_VERIFIER" -> CopySignerPurpose.RestoreCopyVerifier
        | "RESTORE_REPORT" -> CopySignerPurpose.RestoreReport
        | "CHECKPOINT" -> CopySignerPurpose.Checkpoint
        | "WRITER_HANDOFF_ABORT" -> CopySignerPurpose.WriterHandoffAbort
        | _ -> fail HttpInputProblem.InvalidJson

    let private role values =
        let capacity = required "approvalRole" values |> stringValue
        let holder = required "holderApprovalId" values

        match capacity, holder.ValueKind with
        | "CUSTODIAN", JsonValueKind.Null -> CopySignerApprovalRole.Custodian
        | "OWNER", JsonValueKind.String ->
            CopySignerApprovalRole.Owner(holder |> stringValue |> operationIdValue)
        | _ -> fail HttpInputProblem.InvalidJson

    let approve bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties
                    [
                        "approvalId"
                        "signingKeyId"
                        "action"
                        "purpose"
                        "publicKeySha256"
                        "approvalRole"
                        "holderApprovalId"
                        "expiresAt"
                    ]

            {
                ApprovalId = required "approvalId" values |> stringValue |> operationIdValue
                SigningKeyId = required "signingKeyId" values |> stringValue |> operationIdValue
                Action = required "action" values |> stringValue |> action
                Purpose = required "purpose" values |> stringValue |> purpose
                PublicKeySha256 =
                    required "publicKeySha256" values
                    |> stringValue
                    |> digestValue
                    |> Convert.FromHexString
                Role = role values
                ExpiresAt =
                    required "expiresAt" values |> stringValue |> utcMicrosecondTimestampValue
            })
