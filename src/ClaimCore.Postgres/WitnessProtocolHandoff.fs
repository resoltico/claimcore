namespace ClaimCore.Postgres

open System.Threading
open ClaimCore.Witness

module internal WitnessProtocolHandoff =
    let verifySettlement (witness: WitnessProtocol) eventId sequence hash (ct: CancellationToken) =
        task {
            let! observed = witness.EvidenceStore.TryReadEvidence(eventId, SettledAuthority, ct)
            let evidence = observed |> Option.defaultWith (fun () -> raise WitnessPending)

            if
                evidence.Ticket.Sequence <> sequence
                || evidence.Ticket.EntryHash <> hash
                || evidence.Ticket.ScopeKind <> Installation
                || evidence.Ticket.SubjectCaseId.IsSome
            then
                raise WitnessPending
        }
