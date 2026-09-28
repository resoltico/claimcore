namespace ClaimCore.Postgres

open System
open System.Text.Json

/// Exact canonical transition shape; authorization and evidence checks happen at owner admission.
module internal ManagedCopyTransitionAttestation =
    let private fields =
        ManagedCopyRegistrationAttestation.fields
        |> Set.add "actionWitnessCutoffSequence"
        |> Set.add "actionWitnessCutoffHash"
        |> Set.add "deletionApprovalId"

    let private shape (root: JsonElement) bytes =
        let properties = root.EnumerateObject() |> Seq.toArray

        root.ValueKind = JsonValueKind.Object
        && properties.Length = fields.Count
        && (properties |> Seq.map _.Name |> Set.ofSeq) = fields
        && ManagedCopyRegistrationAttestation.canonicalBytes root = bytes
        && ManagedCopyRegistrationAttestation.requiredText root "format" =
            "claimcore-managed-copy-transition-1"
        && ManagedCopyRegistrationAttestation.requiredText root "primaryRegistration" = "REGISTERED"

    let private supported
        (kind: string)
        (state: string)
        (proof: byte array option)
        (lastVerified: DateTimeOffset option)
        (deletion: byte array option)
        (approval: Guid option)
        =
        match kind, state with
        | "VERIFY", "RETAINED" ->
            proof.IsSome && lastVerified.IsSome && deletion.IsNone && approval.IsNone
        | "DELETE_REQUEST", "DELETE_PENDING"
        | "UNKNOWN", "UNKNOWN" ->
            deletion.IsNone && approval.IsNone && (proof.IsNone || lastVerified.IsSome)
        | "VERIFIED_DELETED", "VERIFIED_DELETED" ->
            deletion.IsSome && approval.IsSome && lastVerified.IsSome
        | _ -> false

    let private valid (transition: ManagedCopyTransition) =
        let copy = transition.Copy

        transition.Revision >= 2L
        && transition.ActionWitnessCutoffSequence >= 0L
        && transition.ActionWitnessCutoffHash.Length = 32
        && ManagedCopyRegistrationAttestation.expectedKind copy
        && copy.Epoch > 0L
        && copy.CiphertextBytes > 0L
        && copy.WitnessCutoffSequence >= 0L
        && copy.RetainUntil > copy.CapturedAt
        && copy.RetainUntil <= copy.CapturedAt.AddYears(10)
        && (transition.LastVerifiedAt
            |> Option.forall (fun instant -> instant >= copy.CapturedAt))
        && supported
            transition.EventKind
            transition.State
            copy.VerificationProofSha256
            transition.LastVerifiedAt
            transition.DeletionProofSha256
            transition.DeletionApprovalId

    let private decode (root: JsonElement) =
        {
            Copy = ManagedCopyRegistrationAttestation.decode root
            Revision = ManagedCopyRegistrationAttestation.number root "copyRevision"
            EventKind = ManagedCopyRegistrationAttestation.requiredText root "eventKind"
            State = ManagedCopyRegistrationAttestation.requiredText root "state"
            PreviousEventHash =
                ManagedCopyRegistrationAttestation.requiredText root "previousEventHash"
                |> ManagedCopyRegistrationAttestation.hex
            ActionWitnessCutoffSequence =
                ManagedCopyRegistrationAttestation.number root "actionWitnessCutoffSequence"
            ActionWitnessCutoffHash =
                ManagedCopyRegistrationAttestation.requiredText root "actionWitnessCutoffHash"
                |> ManagedCopyRegistrationAttestation.hex
            LastVerifiedAt = ManagedCopyRegistrationAttestation.optionalTime root "lastVerifiedAt"
            DeletionProofSha256 =
                ManagedCopyRegistrationAttestation.optionalHex root "deletionProofSha256"
            DeletionApprovalId =
                ManagedCopyRegistrationAttestation.optionalUuid root "deletionApprovalId"
        }

    let parse (bytes: byte array) =
        if isNull (box bytes) || bytes.Length = 0 || bytes.Length > 300000 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                let root = document.RootElement

                if shape root bytes then
                    let transition = decode root
                    if valid transition then Some transition else None
                else
                    None
            with _ ->
                None
