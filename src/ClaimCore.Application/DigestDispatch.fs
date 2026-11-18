namespace ClaimCore.Application

open System.Threading
open System.Threading.Tasks
open ClaimCore.Application.Storage
open ClaimCore.Domain

module internal DigestDispatch =
    let run
        source
        installation
        basis
        (send: EmailAddress -> Map<DigestCountKind, int64> -> CancellationToken -> Task<bool>)
        (record: ActorId -> DigestOutcome -> unit)
        token
        =
        task {
            let today = basis.BusinessDate
            let! candidates = DigestStorage.candidates source installation today token

            for actor in candidates do
                let! claimed = DigestStorage.claim source installation today actor token

                if claimed then
                    let! prepared = DigestStorage.prepare source installation basis actor token

                    let! outcome =
                        task {
                            match prepared with
                            | Some(recipient, counts) when not counts.IsEmpty ->
                                let! accepted = send recipient counts token

                                return
                                    if accepted then
                                        DigestOutcome.Sent
                                    else
                                        DigestOutcome.Failed
                            | _ -> return DigestOutcome.SkippedEmpty
                        }

                    do! DigestStorage.complete source installation today actor outcome token
                    record actor outcome
        }
