namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application.Storage
open ClaimCore.Domain

module internal DigestDispatch =
    let run
        source
        installation
        basis
        (send:
            Released<EmailAddress * Map<DigestCountKind, int64>>
                -> CancellationToken
                -> Task<DigestRelayResult>)
        (authorizeRelease:
            ActorId
                -> CancellationToken
                -> EmailAddress * Map<DigestCountKind, int64>
                -> Task<Released<EmailAddress * Map<DigestCountKind, int64>> option>)
        (record: ActorId -> DigestOutcome -> unit)
        (token: CancellationToken)
        =
        let finish actor claim outcome =
            task {
                use cleanup = new CancellationTokenSource(TimeSpan.FromSeconds 5.)

                try
                    let! settled =
                        DigestStorage.complete source installation claim outcome cleanup.Token

                    settled |> Option.iter (record actor)
                with error ->
                    // A later dispatcher reconciles claims whose DB completion is still unavailable.
                    FailureDiagnostics.report error
            }

        let attempt actor claim =
            task {
                let mutable mayHaveSubmitted = false

                try
                    let! prepared = DigestStorage.prepare source installation basis actor token

                    match prepared with
                    | Some(recipient, counts) when not counts.IsEmpty ->
                        let! started = DigestStorage.start source installation claim token

                        match started with
                        | None -> do! finish actor claim DigestOutcome.Unknown
                        | Some remaining ->
                            if remaining <= TimeSpan.Zero || token.IsCancellationRequested then
                                do! finish actor claim DigestOutcome.FailedBeforeAcceptance
                            else
                                use sendDeadline =
                                    CancellationTokenSource.CreateLinkedTokenSource token

                                sendDeadline.CancelAfter remaining

                                let! current =
                                    DigestStorage.prepare
                                        source
                                        installation
                                        basis
                                        actor
                                        sendDeadline.Token

                                if current <> prepared then
                                    do! finish actor claim DigestOutcome.FailedBeforeAcceptance
                                else
                                    match!
                                        authorizeRelease
                                            actor
                                            sendDeadline.Token
                                            (recipient, counts)
                                    with
                                    | Some released when not sendDeadline.IsCancellationRequested ->
                                        mayHaveSubmitted <- true
                                        let! relay = send released sendDeadline.Token

                                        let outcome =
                                            match relay with
                                            | DigestRelayResult.Accepted ->
                                                DigestOutcome.RelayAccepted
                                            | DigestRelayResult.Refused ->
                                                DigestOutcome.FailedBeforeAcceptance
                                            | DigestRelayResult.Uncertain -> DigestOutcome.Unknown

                                        do! finish actor claim outcome
                                    | _ ->
                                        do! finish actor claim DigestOutcome.FailedBeforeAcceptance
                    | _ -> do! finish actor claim DigestOutcome.SkippedEmpty
                with _ ->
                    let outcome =
                        if mayHaveSubmitted then
                            DigestOutcome.Unknown
                        else
                            DigestOutcome.FailedBeforeAcceptance

                    do! finish actor claim outcome
            }

        task {
            do! DigestStorage.reconcile source installation token
            let today = basis.BusinessDate
            let! candidates = DigestStorage.candidates source installation today token

            for actor in candidates do
                if not token.IsCancellationRequested then
                    try
                        let! claim = DigestStorage.claim source installation today actor token

                        match claim with
                        | Some claim -> do! attempt actor claim
                        | None -> ()
                    with error ->
                        FailureDiagnostics.report error

            return! DigestStorage.summary source installation token
        }
