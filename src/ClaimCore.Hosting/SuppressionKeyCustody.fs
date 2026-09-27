namespace ClaimCore.Hosting

open System
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.HostSecurity

/// Adapts the owner-private file to the narrow application commitment port.
module internal SuppressionKeyCustody =
    let load path installationId lineageId expectedKeyId (expectedCheck: byte array) =
        let key = SuppressionKeyFile.Load(path)

        let admit () =
            if key.KeyId <> expectedKeyId || expectedCheck.Length <> 32 then
                invalidOp "Suppression key custody does not match this installation."

            let actual = key.Check(installationId, lineageId)

            if not (CryptographicOperations.FixedTimeEquals(actual, expectedCheck)) then
                invalidOp "Suppression key custody does not match this installation."

        try
            admit ()

            let commitments =
                { new ClaimCore.Application.ISuppressionCommitments with
                    member _.InstallationId = installationId
                    member _.LineageId = lineageId
                    member _.KeyId = key.KeyId
                    member _.Admit() = admit ()

                    member _.Reference(reference) =
                        key.Reference(installationId, lineageId, reference)

                    member _.Operation(operationId) =
                        key.Operation(installationId, lineageId, operationId)

                    member _.RequestCandidate(canonical) =
                        key.RequestCandidate(installationId, lineageId, canonical)

                    member _.PurgeProposal(canonicalDraft) =
                        key.PurgeProposal(installationId, lineageId, canonicalDraft)

                    member _.ApprovalDraft(canonicalDraft) =
                        key.ApprovalDraft(installationId, lineageId, canonicalDraft)

                    member _.ApprovalCanonical(canonicalApproval) =
                        key.ApprovalCanonical(installationId, lineageId, canonicalApproval)
                }

            key :> IDisposable, commitments
        with _ ->
            (key :> IDisposable).Dispose()
            invalidOp "Private suppression key is invalid."
