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

    let exactTarget (console: InteractiveConsole) language target =
        if not console.IsInteractive then
            false
        else
            MessageCatalogs.renderCli language (CliMessage.TypeConfirmationTarget target)
            |> Encoding.UTF8.GetBytes
            |> console.Write

            console.ReadLine()
            |> Option.exists (fun answer -> String.Equals(answer, target, StringComparison.Ordinal))

    let irreversibleTarget invocation =
        match Frames.value Api.operationExecute invocation with
        | Some(OperationInput.Case request) when
            Consequence.ofAct (ActKind.Case(CaseAct.kind request.Act)) = Consequence.Irreversible
            ->
            match request.Target with
            | CaseSelector.Reference reference -> Some reference
            | CaseSelector.Id _ -> invalidOp "Human irreversible commands require a case reference."
        | Some(OperationInput.Authority request) when
            AuthorityAct.kind request.Act = AuthorityKind.EraseProfile
            ->
            Some(request.OperationId.ToString("D"))
        | _ ->
            Frames.value Api.operationResolve invocation
            |> Option.map (fun input -> input.OperationId.ToString("D"))

    let validate isInteractive (prepared: PreparedVerb) =
        irreversibleTarget prepared.Invocation
        |> Option.bind (fun target ->
            match prepared.Confirmation with
            | Some value when value <> target ->
                Some(UsageError.InvalidChoice("--confirm", value, [ target ]))
            | None when not isInteractive -> Some(UsageError.MissingOption "--confirm")
            | _ -> None)

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
            + MessageCatalogs.render
                language
                (OperationCatalog.consequenceId operation |> Option.get)
                Map.empty
            + "\n"

            |> Encoding.UTF8.GetBytes
            |> console.Write

            exactTarget console language (operationId.ToString("D"))
