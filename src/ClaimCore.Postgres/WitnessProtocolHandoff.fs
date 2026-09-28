namespace ClaimCore.Postgres

open ClaimCore.Witness

module internal WitnessProtocolHandoff =
    let verifySettlement (witness: WitnessProtocol) eventId sequence hash =
        let evidence =
            witness.EvidenceStore.TryReadEvidence(eventId, SettledAuthority)
            |> Option.defaultWith (fun () -> raise WitnessPending)

        if
            evidence.Ticket.Sequence <> sequence
            || evidence.Ticket.EntryHash <> hash
            || evidence.Ticket.ScopeKind <> Installation
            || evidence.Ticket.SubjectCaseId.IsSome
        then
            raise WitnessPending
