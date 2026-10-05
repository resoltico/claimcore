namespace ClaimCore.Postgres

open System
open System.Threading
open System.Globalization
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Witness
open WitnessProtocolReconciliation

[<NoEquality; NoComparison>]
type internal PendingRealDataActivationApproval =
    {
        ApprovedAt: DateTimeOffset
        GrantRevision: int64
        Intent: WitnessIntent
    }

/// Reconstruct an intent-only approval from exact witnessed bytes, never a new DB-clock candidate.
module internal RealDataActivationApprovalRecovery =
    let private propertyNames =
        [
            "version"
            "action"
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
            "approverActorId"
            "approverGrantRevision"
            "approvedAt"
            "expiresAt"
        ]

    let private decode
        (request: RealDataActivationApprovalRequest)
        actorId
        (evidence: Evidence)
        (plain: byte array)
        =
        if plain.Length > 8192 then
            invalidOp "Activation approval intent is oversized."

        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(plain))
        let root = document.RootElement
        let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList

        if
            names <> propertyNames
            || root.GetProperty("version").GetInt32() <> 1
            || root.GetProperty("action").GetString() <> "APPROVE_REAL_DATA_ACTIVATION"
            || root.GetProperty("approverActorId").GetGuid() <> actorId
        then
            invalidOp "Activation approval intent differs."

        let revision = root.GetProperty("approverGrantRevision").GetInt64()

        let approvedAt =
            let text =
                root.GetProperty("approvedAt").GetString()
                |> Option.ofObj
                |> Option.defaultWith (fun () -> invalidOp "Approval time is absent.")

            DateTimeOffset.ParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None)

        let expected =
            RealDataActivationApprovalCandidate.canonical request actorId revision approvedAt

        try
            if
                expected <> plain
                || evidence.Ticket.ScopeKind <> ScopeKind.Installation
                || evidence.Ticket.SubjectCaseId.IsSome
                || evidence.Ticket.Sequence <> request.ExpectedWitnessSequence + 1L
            then
                invalidOp "Activation approval intent differs."

            {
                ApprovedAt = approvedAt
                GrantRevision = revision
                Intent =
                    {
                        Ticket = evidence.Ticket
                        CandidateHash = SHA256.HashData(plain)
                    }
            }
        finally
            CryptographicOperations.ZeroMemory(expected)

    let tryRead
        (witness: WitnessProtocol)
        (request: RealDataActivationApprovalRequest)
        actorId
        (ct: CancellationToken)
        =
        task {
            let! observed = witness.EvidenceStore.TryReadEvidence(request.ApprovalId, Intent, ct)

            match observed with
            | None -> return None
            | Some evidence ->
                let plain =
                    witness.KeyCustody.Decrypt(
                        evidence.Ticket.KeyId,
                        witness.AssociatedData(request.ApprovalId, "INTENT"),
                        evidence.EncryptedPayload
                    )

                try
                    return Some(decode request actorId evidence plain)
                finally
                    CryptographicOperations.ZeroMemory(plain)
        }

    let requireSettled
        (witness: WitnessProtocol)
        (request: RealDataActivationApprovalRequest)
        (stored: RealDataActivationApprovalRow)
        canonical
        ct
        =
        task {
            try
                do!
                    witness.VerifyAuthorityEvidenceForInstallation(
                        request.ApprovalId,
                        stored.WitnessSequence,
                        stored.WitnessEpoch,
                        stored.WitnessHash,
                        stored.CandidateHash,
                        ct
                    )
            with _ ->
                do!
                    witness.ReconcileAuthority(
                        request.ApprovalId,
                        stored.WitnessSequence,
                        stored.WitnessEpoch,
                        stored.WitnessHash,
                        canonical,
                        ct
                    )

                do!
                    witness.VerifyAuthorityEvidenceForInstallation(
                        request.ApprovalId,
                        stored.WitnessSequence,
                        stored.WitnessEpoch,
                        stored.WitnessHash,
                        stored.CandidateHash,
                        ct
                    )
        }
