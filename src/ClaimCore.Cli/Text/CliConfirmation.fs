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

    let exactTarget (console: InteractiveConsole) language target =
        if not console.IsInteractive then
            false
        else
            MessageCatalogs.renderCli
                language
                (CliMessage.TypeConfirmationTarget(TerminalText.data target))
            |> Encoding.UTF8.GetBytes
            |> console.Write

            console.ReadLine()
            |> Option.exists (fun answer -> String.Equals(answer, target, StringComparison.Ordinal))

    let irreversibleTarget invocation =
        match Frames.value Api.operationExecute invocation with
        | Some input when Consequence.ofAct input.Task = Consequence.Irreversible ->
            match input.Binding with
            | OperationBinding.Case(CaseSelector.Reference reference, _, _) -> Some reference
            | OperationBinding.People _ -> Some(input.OperationId.ToString("D"))
            | _ -> invalidOp "Human irreversible commands require their confirmation target."
        | _ -> None

    let validate isInteractive (prepared: PreparedVerb) =
        irreversibleTarget prepared.Invocation
        |> Option.bind (fun target ->
            match prepared.Confirmation with
            | Some value when value <> target ->
                Some(UsageError.InvalidChoice("--confirm", value, [ target ]))
            | None when not isInteractive -> Some(UsageError.MissingOption "--confirm")
            | _ -> None)
