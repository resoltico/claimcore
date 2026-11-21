namespace ClaimCore.Cli

open ClaimCore.CommandLine

open System
open System.Text
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
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
            | "ko" -> [ "예"; "네" ]
            | "ja" -> [ "はい" ]
            | "zh-Hans" -> [ "是"; "确认" ]
            | "zh-Hant" -> [ "是"; "確認" ]
            | "es" -> [ "s"; "sí" ]
            | "de" -> [ "j"; "ja" ]
            | _ -> [ "y"; "yes" ]

        answer
        |> Option.exists (fun (answer: string) ->
            words
            |> List.exists (fun word ->
                String.Equals(answer.Trim(), word, StringComparison.OrdinalIgnoreCase)))

    let private promptForTarget (console: InteractiveConsole) language shown target =
        if not console.IsInteractive then
            false
        else
            MessageCatalogs.renderCli language (CliMessage.TypeConfirmationTarget shown)
            |> Encoding.UTF8.GetBytes
            |> console.Write

            console.ReadLine()
            |> Option.exists (fun answer -> String.Equals(answer, target, StringComparison.Ordinal))

    let exactTarget console language target =
        promptForTarget console language (TerminalText.data target) target

    let exactConfirmation console language target =
        let text = ConfirmationTarget.text target

        match target with
        | ConfirmationTarget.Principal _ -> promptForTarget console language text text
        | _ -> exactTarget console language text

    let requiredTarget invocation =
        match Frames.value Api.operationExecute invocation with
        | Some input when OperationSubmission.requiresPrincipalConfirmation input.Task ->
            OperationSubmission.principalConfirmation input
            |> Option.map (PrincipalKey.toInput >> _.Subject >> ConfirmationTarget.principal)
        | Some input when TaskConsequences.ofTask input.Task = Consequence.Irreversible ->
            match input.Envelope.Binding with
            | OperationBinding.Case(CaseSelector.Reference reference, _, _, _) ->
                CaseReference.parse reference
                |> Result.toOption
                |> Option.map ConfirmationTarget.CaseReference
            | OperationBinding.Case(CaseSelector.Id _, _, _, _) -> None
            | OperationBinding.People _ -> Some(ConfirmationTarget.Operation input.OperationId)
            | _ -> invalidOp "Human irreversible commands require their confirmation target."
        | _ -> None

    let validate isInteractive (prepared: PreparedVerb) =
        requiredTarget prepared.Invocation
        |> Option.map ConfirmationTarget.text
        |> Option.bind (fun target ->
            match prepared.Confirmation with
            | Some value when value <> target ->
                Some(UsageError.InvalidChoice("--confirm", value, [ target ]))
            | None when not isInteractive -> Some(UsageError.MissingOption "--confirm")
            | _ -> None)

    let needsReferencePreview invocation =
        match Frames.value Api.operationExecute invocation with
        | Some input when TaskConsequences.ofTask input.Task = Consequence.Irreversible ->
            match input.Envelope.Binding with
            | OperationBinding.Case(CaseSelector.Id _, _, _, _) -> true
            | _ -> false
        | _ -> false
