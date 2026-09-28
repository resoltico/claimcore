namespace ClaimCore.Database

open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness

/// Custody publication and adopted-copy transitions remain owner-only, not case-work commands.
module internal DatabaseCopyCustodyExecution =
    let route ownerConnection command (connection: NpgsqlConnection) (witness: WitnessProtocol) =
        match command with
        | DatabaseCommand.AdoptManagedCopy proposal ->
            DatabaseCopyAdoptionExecution.run ownerConnection connection witness proposal
            |> Some
        | DatabaseCommand.PublishExternalCopy proposal ->
            DatabaseExternalPublicationExecution.run ownerConnection connection witness proposal
            |> Some
        | DatabaseCommand.TransitionAdoptedCopy(canonical, signature) ->
            DatabaseAdoptedTransitionExecution.run connection witness false canonical signature
            |> Some
        | DatabaseCommand.VerifyDeleteAdoptedCopy(canonical, signature) ->
            DatabaseAdoptedTransitionExecution.run connection witness true canonical signature
            |> Some
        | _ -> None
