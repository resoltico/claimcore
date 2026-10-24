namespace ClaimCore.Database

open System
open System.Threading
open ClaimCore.Postgres

module Execution =
    let private perform (administration: Administration) command =
        task {
            match command with
            | Initialize zone ->
                let! id = administration.Initialize(zone, CancellationToken.None)

                return
                    Replies.encode
                        {|
                            tag = "INITIALIZED"
                            installationId = id
                        |},
                    0
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

                return
                    Replies.encode
                        {|
                            tag = "HEAD"
                            position = Replies.position head
                        |},
                    0
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
                with
                | :? System.Text.Json.JsonException -> return Replies.tag "REFUSED" 2
                | error ->
                    return
                        match AdministrationFailures.classify error with
                        | AdministrationFailure.Refused -> Replies.tag "REFUSED" 2
                        | AdministrationFailure.OutcomeUnknown -> Replies.tag "OUTCOME_UNKNOWN" 4
                        | AdministrationFailure.Failed -> Replies.tag "FAILED" 3
        }
