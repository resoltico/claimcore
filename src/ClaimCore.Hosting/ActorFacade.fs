namespace ClaimCore.Hosting

open ClaimCore.Application

module internal ActorFacade =
    let private run (gate: AdmissionGate) refused action =
        task {
            match gate.Acquire() with
            | None -> return refused
            | Some lease ->
                use _lease = lease
                return! action ()
        }

    let bind gate (core: IActorClaimsCore) =
        let query () =
            QueryOutcome.Failed CoreFault.StoreUnavailable

        let execute = ExecuteOutcome.Failed CoreFault.StoreUnavailable

        { new IActorClaimsCore with
            member _.Context token =
                run gate (query ()) (fun () -> core.Context token)

            member _.Preview(request, token) =
                run gate (PreviewOutcome.Failed CoreFault.StoreUnavailable) (fun () ->
                    core.Preview(request, token))

            member _.Execute(request, token) =
                run gate execute (fun () -> core.Execute(request, token))

            member _.ExecuteAuthority(request, token) =
                run gate execute (fun () -> core.ExecuteAuthority(request, token))

            member _.Get(reference, token) =
                run gate (query ()) (fun () -> core.Get(reference, token))

            member _.List(request, token) =
                run gate (query ()) (fun () -> core.List(request, token))

            member _.History(request, token) =
                run gate (query ()) (fun () -> core.History(request, token))

            member _.Observe(id, token) =
                run gate (query ()) (fun () -> core.Observe(id, token))

            member _.ListGrants(request, token) =
                run gate (query ()) (fun () -> core.ListGrants(request, token))

            member _.ListActors(request, token) =
                run gate (query ()) (fun () -> core.ListActors(request, token))
        }
