namespace ClaimCore.Database

open ClaimCore.Domain

open ClaimCore.Contract

open System
open System.Globalization
open System.Text
open ClaimCore.Json
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.HostSecurity

type Command =
    | Initialize of string
    | ProvisionOwner
    | Verify of ExpectedPosition option
    | Head
    | Help
    | Version

[<RequireQualifiedAccess>]
type CommandName =
    | Initialize
    | ProvisionOwner
    | Verify
    | Head
    | Help
    | Version

module Commands =
    let vocabulary =
        Vocabulary.ofUnion<CommandName> "command" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary

    let token name =
        Vocabulary.token vocabulary name
        |> _.ToLowerInvariant()
        |> fun value -> value.Replace('_', '-')

    let parse value =
        all |> List.tryFind (fun name -> token name = value)

    let synopsis =
        function
        | CommandName.Initialize -> "claimcore-database initialize TIME_ZONE"
        | CommandName.ProvisionOwner -> "claimcore-database provision-owner"
        | CommandName.Verify -> "claimcore-database verify [--expect SEQUENCE:HASH]"
        | CommandName.Head -> "claimcore-database head"
        | CommandName.Help -> "claimcore-database help"
        | CommandName.Version -> "claimcore-database version"

[<RequireQualifiedAccess>]
type UsageIssue =
    | UnknownCommand
    | ArgumentsInvalid
    | CheckpointInvalid

type UsageError =
    {
        Issue: UsageIssue
        Command: CommandName option
    }

module Inputs =
    let private expected (text: string) =
        let parts = text.Split(':')
        let mutable sequence = 0L

        if
            parts.Length = 2
            && Int64.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, &sequence)
            && sequence >= 0L
            && sequence.ToString(CultureInfo.InvariantCulture) = parts[0]
            && parts[1].Length = 64
            && parts[1]
               |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
        then
            let position: ExpectedPosition =
                {
                    Sequence = sequence
                    EntryHash = Convert.FromHexString(parts[1])
                }

            if ExpectedPositions.isValid position then
                Some position
            else
                None
        else
            None

    let private noArguments command arguments =
        if List.isEmpty arguments then
            Ok command
        else
            Error UsageIssue.ArgumentsInvalid

    let private initialize arguments =
        match arguments with
        | [ zone ] -> Ok(Initialize zone)
        | _ -> Error UsageIssue.ArgumentsInvalid

    let private verify arguments =
        match arguments with
        | [] -> Ok(Verify None)
        | [ "--expect"; value ] ->
            expected value
            |> Option.map (Some >> Verify >> Ok)
            |> Option.defaultValue (Error UsageIssue.CheckpointInvalid)
        | _ -> Error UsageIssue.ArgumentsInvalid

    let private parse name arguments =
        match name with
        | CommandName.Help -> noArguments Help arguments
        | CommandName.Version -> noArguments Version arguments
        | CommandName.Initialize -> initialize arguments
        | CommandName.ProvisionOwner -> noArguments ProvisionOwner arguments
        | CommandName.Head -> noArguments Head arguments
        | CommandName.Verify -> verify arguments

    let command arguments =
        match arguments with
        | [] -> Ok Help
        | first :: rest ->
            match Commands.parse first with
            | None ->
                Error
                    {
                        Issue = UsageIssue.UnknownCommand
                        Command = None
                    }
            | Some name ->
                parse name rest
                |> Result.mapError (fun issue -> { Issue = issue; Command = Some name })

    let private bytes setting maximum =
        Environment.GetEnvironmentVariable setting
        |> Option.ofObj
        |> Option.map (fun path ->
            PrivateFile.read maximum path
            |> Result.mapError (fun _ -> "Private input was refused."))
        |> Option.defaultValue (Error "Required private input is absent.")
        |> Result.defaultWith invalidOp

    let connection () =
        let value = bytes "CLAIMCORE_ADMIN_CONNECTION_FILE" 16384

        try
            UTF8Encoding(false, true).GetString(value).Trim()
        finally
            CryptographicOperations.ZeroMemory value

    let private ownerInput =
        Codec.record (fun operationId issuer subject -> operationId, issuer, subject)
        |> Codec.field "operationId" (fun (id, _, _) -> id) Codec.uuid
        |> Codec.field "issuer" (fun (_, issuer, _) -> issuer) Codec.text
        |> Codec.field "subject" (fun (_, _, subject) -> subject) Codec.text
        |> Codec.build

    let decodeOwner (value: byte array) =
        Codec.decode 8192 ownerInput value
        |> Result.mapError (fun _ -> "Owner input is invalid.")
        |> Result.bind (fun (operationId, issuer, subject) ->
            // This format denotes a human principal; PrincipalKey owns its admission.
            PrincipalKey.human issuer subject
            |> Result.mapError (fun _ -> "Owner principal is invalid.")
            |> Result.map (fun principal -> operationId, principal))
        |> Result.defaultWith invalidOp

    let owner () =
        let value = bytes "CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE" 8192

        try
            decodeOwner value
        finally
            CryptographicOperations.ZeroMemory value
