namespace ClaimCore.Database

open System
open System.Globalization
open System.Text
open System.Text.Json
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.HostSecurity

type Command =
    | Initialize of string
    | Migrate
    | ProvisionOwner
    | Verify of ExpectedPosition option
    | Head
    | Help
    | Version

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

    let command =
        function
        | []
        | [ "help" ] -> Some Help
        | [ "version" ] -> Some Version
        | [ "initialize"; zone ] -> Some(Initialize zone)
        | [ "migrate" ] -> Some Migrate
        | [ "provision-owner" ] -> Some ProvisionOwner
        | [ "verify" ] -> Some(Verify None)
        | [ "verify"; "--expect"; position ] -> expected position |> Option.map (Some >> Verify)
        | [ "head" ] -> Some Head
        | _ -> None

    let private bytes setting maximum =
        match Environment.GetEnvironmentVariable setting with
        | null -> invalidOp "Required private input is absent."
        | path ->
            PrivateFile.read maximum path
            |> Result.defaultWith (fun failure ->
                if failure = PrivateFileFailure.NativeAssetUnavailable then
                    Console.Error.WriteLine PrivateFile.nativeAssetGuidance

                invalidOp "Private input was refused.")

    let connection () =
        let value = bytes "CLAIMCORE_ADMIN_CONNECTION_FILE" 16384

        try
            UTF8Encoding(false, true).GetString(value).Trim()
        finally
            CryptographicOperations.ZeroMemory value

    let decodeOwner (value: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory value)
        let root = document.RootElement
        let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.sort |> Seq.toList

        if names <> [ "issuer"; "operationId"; "subject" ] then
            invalidOp "Owner input properties differ."

        let text name =
            root.GetProperty(name: string).GetString()
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Owner value is absent.")

        let operationText = text "operationId"
        let mutable operationId = Guid.Empty

        if
            not (Guid.TryParseExact(operationText, "D", &operationId))
            || operationId = Guid.Empty
            || operationId.ToString("D") <> operationText
        then
            invalidOp "Owner operation identifier is invalid."

        let principal =
            PrincipalKey.human (text "issuer") (text "subject")
            |> Result.defaultWith (fun _ -> invalidOp "Owner principal is invalid.")

        operationId, principal

    let owner () =
        let value = bytes "CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE" 8192

        try
            decodeOwner value
        finally
            CryptographicOperations.ZeroMemory value
