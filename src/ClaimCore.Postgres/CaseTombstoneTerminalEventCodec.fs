namespace ClaimCore.Postgres

open System
open System.Globalization
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal TerminalEventDecoded =
    {
        Proposal: TombstoneTerminalProposal
        CopyProofSha256: byte array
        RecoveryFenceProofSha256: byte array option
        ApprovalOneId: Guid
        ApprovalTwoId: Guid
        ActorAuthorityRevision: int64
        AuthorityRevision: int64
        PreviousAuthorityHash: byte array
        ObservedAt: DateTimeOffset
    }

module internal CaseTombstoneTerminalEventCodec =
    let private names =
        [|
            "version"
            "kind"
            "eventId"
            "caseId"
            "executorKind"
            "proposal"
            "copyProofSha256"
            "recoveryFenceProofSha256"
            "approvalOneId"
            "approvalTwoId"
            "actorAuthorityRevision"
            "authorityRevision"
            "previousAuthorityHash"
            "observedUtcInstant"
        |]

    let private text (root: JsonElement) (name: string) =
        root.GetProperty(name).GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Terminal owner event text is null.")

    let private fenceProof (root: JsonElement) =
        let value = root.GetProperty("recoveryFenceProofSha256")

        match value.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String -> Some(value.GetBytesFromBase64())
        | _ -> invalidOp "Terminal recovery proof shape is invalid."

    let private fields (root: JsonElement) proposal =
        {
            Proposal = proposal
            CopyProofSha256 = root.GetProperty("copyProofSha256").GetBytesFromBase64()
            RecoveryFenceProofSha256 = fenceProof root
            ApprovalOneId = root.GetProperty("approvalOneId").GetGuid()
            ApprovalTwoId = root.GetProperty("approvalTwoId").GetGuid()
            ActorAuthorityRevision = root.GetProperty("actorAuthorityRevision").GetInt64()
            AuthorityRevision = root.GetProperty("authorityRevision").GetInt64()
            PreviousAuthorityHash = root.GetProperty("previousAuthorityHash").GetBytesFromBase64()
            ObservedAt =
                DateTimeOffset.ParseExact(
                    text root "observedUtcInstant",
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None
                )
        }

    let private valid (root: JsonElement) (value: TerminalEventDecoded) =
        let copy = TombstoneTerminalProposal.copy value.Proposal
        let fields = root.EnumerateObject() |> Seq.map _.Name |> Seq.toArray

        fields = names
        && root.GetProperty("version").GetInt32() = 1
        && text root "kind" = "CASE_TERMINAL_OWNER_EVENT"
        && text root "executorKind" = "SCHEMA_OWNER_PROCESS"
        && root.GetProperty("eventId").GetGuid() = copy.EventId
        && root.GetProperty("caseId").GetGuid() = copy.CaseId
        && value.CopyProofSha256.Length = 32
        && (value.RecoveryFenceProofSha256 |> Option.forall (fun proof -> proof.Length = 32))
        && value.ApprovalOneId <> Guid.Empty
        && value.ApprovalTwoId <> Guid.Empty
        && value.ApprovalOneId <> value.ApprovalTwoId
        && value.ActorAuthorityRevision > 0L
        && value.AuthorityRevision > 0L
        && value.PreviousAuthorityHash.Length = 32
        && value.ObservedAt.Offset = TimeSpan.Zero
        && (match value.Proposal, value.RecoveryFenceProofSha256 with
            | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _, None -> true
            | TombstoneTerminalProposal.CompleteSuppressionHorizon _, Some _ -> true
            | _ -> false)

    let decode (canonical: byte array) =
        if canonical.Length < 2 || canonical.Length > 16384 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(canonical))
                let root = document.RootElement
                let embedded = root.GetProperty("proposal").GetBytesFromBase64()

                try
                    match CaseTombstoneTerminalProposalCodec.decode embedded with
                    | None -> None
                    | Some proposal ->
                        let value = fields root proposal

                        if not (valid root value) then
                            None
                        else
                            let rebuilt =
                                CaseTombstoneTerminalEventCandidate.encodeProof
                                    proposal
                                    value.CopyProofSha256
                                    value.RecoveryFenceProofSha256
                                    value.ApprovalOneId
                                    value.ApprovalTwoId
                                    value.ActorAuthorityRevision
                                    (value.AuthorityRevision - 1L)
                                    value.PreviousAuthorityHash
                                    value.ObservedAt

                            try
                                if canonical = rebuilt then Some value else None
                            finally
                                CryptographicOperations.ZeroMemory(rebuilt)
                finally
                    CryptographicOperations.ZeroMemory(embedded)
            with _ ->
                None
