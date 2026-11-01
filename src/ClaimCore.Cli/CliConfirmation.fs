namespace ClaimCore.Cli

open System
open System.Text
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Application.Contracts

[<NoEquality; NoComparison>]
type internal InteractiveConsole =
    {
        IsInteractive: bool
        ReadLine: unit -> string option
        Write: byte array -> unit
    }

module internal CliConfirmation =
    let confirmed language answer =
        let words =
            match language with
            | "lv" -> [ "j"; "jā" ]
            | "ar" -> [ "نعم" ]
            | _ -> [ "y"; "yes" ]

        answer
        |> Option.exists (fun (answer: string) ->
            words
            |> List.exists (fun word ->
                String.Equals(answer.Trim(), word, StringComparison.OrdinalIgnoreCase)))

    let settlement
        (console: InteractiveConsole)
        language
        (context: ActorContext)
        (operationId: Guid)
        =
        if not console.IsInteractive then
            false
        else
            let operation = CatalogOperation.Query QueryEndpoint.Resolve

            MessageCatalogs.renderCli
                language
                (CliMessage.SettlementScope(
                    ActorId.format context.ActorId,
                    context.InstallationId.ToString("D")
                ))
            + "\n"
            + MessageCatalogs.render
                language
                (InteractionInput.messageKey
                    (InteractionInput.Target InputTarget.OperationId)
                    "label")
                Map.empty
            + ": "
            + operationId.ToString("D")
            + "\n"
            + MessageCatalogs.render language (OperationCatalog.meaningId operation) Map.empty
            + "\n"
            + MessageCatalogs.renderCli language CliMessage.ReviewConfirmation
            |> Encoding.UTF8.GetBytes
            |> console.Write

            confirmed language (console.ReadLine())
