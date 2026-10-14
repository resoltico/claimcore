namespace ClaimCore.Hosting

open System.Threading.Tasks
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
        let authority = AuthorityOutcome.Failed CoreFault.StoreUnavailable

        let management =
            { new IAuthorityManagement with
                member _.RegisterActor(id, target, token) =
                    run gate authority (fun () -> core.Authority.RegisterActor(id, target, token))

                member _.SetGrant(id, target, role, scope, active, token) =
                    run gate authority (fun () ->
                        core.Authority.SetGrant(id, target, role, scope, active, token))

                member _.SetEnabled(id, target, enabled, token) =
                    run gate authority (fun () ->
                        core.Authority.SetEnabled(id, target, enabled, token))

                member _.Observe(id, token) =
                    run gate authority (fun () -> core.Authority.Observe(id, token))
            }

        { new IActorClaimsCore with
            member _.Definition token =
                run gate (query ()) (fun () -> core.Definition token)

            member _.Preview(request, token) =
                run gate (PreviewOutcome.Failed CoreFault.StoreUnavailable) (fun () ->
                    core.Preview(request, token))

            member _.Execute(request, token) =
                run gate execute (fun () -> core.Execute(request, token))

            member _.Steward(request, token) =
                run gate execute (fun () -> core.Steward(request, token))

            member _.Get(reference, token) =
                run gate (query ()) (fun () -> core.Get(reference, token))

            member _.List(request, token) =
                run gate (query ()) (fun () -> core.List(request, token))

            member _.History(request, token) =
                run gate (query ()) (fun () -> core.History(request, token))

            member _.ObserveOperation(id, token) =
                run gate (query ()) (fun () -> core.ObserveOperation(id, token))

            member _.Authority = management
        }
