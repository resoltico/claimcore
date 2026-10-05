namespace ClaimCore.Postgres

open System.Threading
open ClaimCore.Witness
open DataAuditCommon

/// Rechecks the owner function's closed post-cutoff authority set from immutable metadata.
module internal CaseTombstonePrunePostcutoffAudit =
    [<RequireQualifiedAccess>]
    type private Kind =
        | First
        | Second
        | Prune

    let private approval (ticket: Ticket) (value: PruneApprovalReceipt) =
        if ticket.OperationId <> value.ApprovalId then
            false
        else
            match ticket.Phase with
            | Intent when
                ticket.Sequence = value.WitnessSequence && ticket.EntryHash = value.WitnessHash
                ->
                true
            | SettledAuthority -> true
            | _ -> corrupt ()

    let private prune (ticket: Ticket) (receipt: StoredWitnessPruneReceipt) =
        if ticket.OperationId <> receipt.EventId then
            corrupt ()

        match ticket.Phase with
        | Intent when
            ticket.Sequence = receipt.IntentSequence
            && ticket.EntryHash = receipt.IntentHash
            ->
            Kind.Prune
        | SettledAuthority -> Kind.Prune
        | _ -> corrupt ()

    let private classify ticket first second receipt =
        if approval ticket first then Kind.First
        elif approval ticket second then Kind.Second
        else prune ticket receipt

    let private page (witness: WitnessProtocol) after previous cutoff ct =
        witnessProofAsync (fun () ->
            witness.EvidenceStore.ReadMetadataPage(after, previous, cutoff, 32, ct))

    let private requireSettlement
        (witness: WitnessProtocol)
        (receipt: StoredWitnessPruneReceipt)
        ct
        =
        task {
            let! observed =
                witness.EvidenceStore.TryReadMetadataOperation(
                    receipt.EventId,
                    SettledAuthority,
                    ct
                )

            let settled = observed |> Option.defaultWith corrupt

            if
                settled.Ticket.ScopeKind <> Case
                || settled.Ticket.SubjectCaseId <> Some receipt.CaseId
                || settled.Ticket.Sequence <= receipt.IntentSequence
                || not settled.PayloadPresent
            then
                corrupt ()

            return settled
        }

    let verify
        (witness: WitnessProtocol)
        (receipt: StoredWitnessPruneReceipt)
        (approvals: PruneApprovalReceipt list)
        (ct: CancellationToken)
        =
        task {
            let! settled = requireSettlement witness receipt ct

            let first = approvals[0]
            let second = approvals[1]
            let mutable one = 0
            let mutable two = 0
            let mutable prune = 0
            let mutable after = receipt.CutoffSequence
            let mutable previous = receipt.CutoffHash

            while after < settled.Ticket.Sequence do
                let! entries = page witness after previous settled.Ticket.Sequence ct

                if entries.Items.IsEmpty then
                    corrupt ()

                for item in entries.Items do
                    let ticket = item.Ticket

                    if ticket.ScopeKind = Case && ticket.SubjectCaseId = Some receipt.CaseId then
                        if not item.PayloadPresent then
                            corrupt ()

                        match classify ticket first second receipt with
                        | Kind.First -> one <- one + 1
                        | Kind.Second -> two <- two + 1
                        | Kind.Prune -> prune <- prune + 1

                    after <- ticket.Sequence
                    previous <- ticket.EntryHash

            if
                after <> settled.Ticket.Sequence
                || previous <> settled.Ticket.EntryHash
                || one <> 2
                || two <> 2
                || prune <> 2
            then
                corrupt ()
        }
