namespace ClaimCore.Database

open System
open ClaimCore.Contract
open ClaimCore.Domain
open System.Threading
open ClaimCore.Postgres

module Execution =
    let private perform (administration: Administration) command =
        task {
            match command with
            | Initialize zone ->
                let! id = administration.Initialize(zone, CancellationToken.None)

                return Replies.initialized id
            | ProvisionOwner ->
                let operationId, principal = Inputs.owner ()

                let! result =
                    administration.ProvisionOwner(operationId, principal, CancellationToken.None)

                return Replies.authority result
            | Verify expected ->
                let! result = administration.Verify(expected, CancellationToken.None)
                return Replies.verification result
            | Head ->
                let! head = administration.Head(CancellationToken.None)

                return Replies.head head
            | Help
            | Version -> return invalidOp "Discovery does not open administration."
        }

    let run command =
        task {
            match command with
            | Help -> return Replies.help ()
            | Version -> return Replies.version ()
            | _ ->
                try
                    use administration = new Administration(Inputs.connection ())
                    return! perform administration command
                with error ->
                    return
                        match AdministrationFailures.classify error with
                        | AdministrationFailure.Refused -> Replies.rejected Refusal.ActNotPermitted
                        | AdministrationFailure.OutcomeUnknown -> Replies.unknown None
                        | AdministrationFailure.Failed -> Replies.failed CoreFault.StoreUnavailable
        }
