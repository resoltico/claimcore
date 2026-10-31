namespace ClaimCore.Cli

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json

[<NoEquality; NoComparison>]
type internal InteractiveConsole =
    {
        IsInteractive: bool
        ReadLine: unit -> string option
        Write: byte array -> unit
    }

[<NoEquality; NoComparison>]
type internal InteractiveTransport =
    {
        Exchange: Invocation -> CancellationToken -> Task<CliReply>
        Deliver: Invocation -> CliReply -> unit
    }

/// A structurally checked draft has no executable revision or automatically allocated operation id.
[<NoEquality; NoComparison>]
type internal InteractiveCaseDraft =
    private
        {
            Reference: string
            Act: AuthoredCaseAct
            SuppliedId: Guid option
            DisplayLanguage: string
            PreviewOnly: bool
        }

    member this.Language = this.DisplayLanguage

module internal InteractiveReview =
    let private hasOption name arguments =
        arguments
        |> List.exists (fun (value: string) ->
            value = name || value.StartsWith(name + "=", StringComparison.Ordinal))

    let private requestedText arguments =
        let separate =
            arguments
            |> List.pairwise
            |> List.choose (fun (name, value) -> if name = "--format" then Some value else None)

        let assigned =
            arguments
            |> List.choose (fun (value: string) ->
                if value.StartsWith("--format=", StringComparison.Ordinal) then
                    Some(value.Substring(9))
                else
                    None)

        match separate @ assigned with
        | []
        | [ "text" ] -> true
        | _ -> false

    let private operation arguments =
        OperationCatalog.mutations
        |> List.tryFind (fun entry ->
            let words = CommandGrammar.words entry

            words = List.truncate words.Length arguments
            && match entry.SelectedAct with
               | Some(ActKind.Case kind) ->
                   (CaseActKind.describe kind).Revision = RevisionInput.Expected
               | _ -> false)

    let private keepGroups entry arguments =
        let inputs, _ = CommandGrammar.derive entry

        let defaults =
            inputs
            |> List.collect (fun input ->
                let flag = "--" + CommandGrammar.kebab (CommandGrammar.inputName input.Name)

                match input.Binding.Kind with
                | BindingKind.Group _ when not (hasOption flag arguments) ->
                    [
                        flag
                        CommandGrammar.kebab (
                            CorrectionGroupAction.token CorrectionGroupAction.Keep
                        )
                    ]
                | _ -> [])

        arguments @ defaults

    let prepare console arguments =
        if
            not console.IsInteractive
            || not (requestedText arguments)
            || hasOption "--expected-revision" arguments
            || hasOption "--dry-run" arguments
        then
            Ok None
        else
            match operation arguments with
            | None -> Ok None
            | Some entry ->
                Grammar.caseIntent (keepGroups entry arguments)
                |> Result.map (fun ((reference, act, supplied), language, preview) ->
                    Some
                        {
                            Reference =
                                match reference with
                                | CaseSelector.Reference reference -> reference
                                | CaseSelector.Id _ ->
                                    invalidOp
                                        "Interactive reference bindings must supply a reference."
                            Act = act
                            SuppliedId = supplied
                            DisplayLanguage = language
                            PreviewOnly = preview
                        })

    let private invocation (endpoint: Endpoint<'Input, 'Output>) input =
        Frames.create endpoint input
        |> Result.defaultWith (fun _ ->
            invalidOp "A typed review step must produce an admitted invocation.")

    let private unpack (endpoint: Endpoint<'Input, 'Output>) (reply: CliReply) =
        match reply.Frame.Result with
        | FrameResult.Reply value when value.Route.Id = endpoint.Id ->
            ServiceReply.value endpoint value
        | FrameResult.Reply _ -> invalidOp "A review response must retain its requested endpoint."
        | _ -> None

    let private confirmed language answer =
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

    let private review
        console
        transport
        (draft: InteractiveCaseDraft)
        (request: CaseOperationRequest)
        installation
        token
        =
        task {
            let input = OperationInput.Case request
            let preview = invocation Api.operationPreview input
            let! response = transport.Exchange preview token
            transport.Deliver preview response

            match unpack Api.operationPreview response with
            | Some {
                       Outcome = Outcome.Succeeded(Previewed.Reviewed _)
                   } when not draft.PreviewOnly ->
                MessageCatalogs.renderCli draft.Language CliMessage.ReviewConfirmation
                |> Encoding.UTF8.GetBytes
                |> console.Write

                if confirmed draft.Language (console.ReadLine()) then
                    let execute =
                        invocation Api.operationExecute input
                        |> Frames.bindInstallation installation
                        |> Result.defaultWith (fun _ ->
                            invalidOp "Reviewed scope must bind the request.")

                    let! accepted = transport.Exchange execute token
                    transport.Deliver execute accepted
                    return accepted.ExitCode
                else
                    return ExitCode.value ExitCode.Success
            | _ -> return response.ExitCode
        }

    let rec run newId console transport (draft: InteractiveCaseDraft) token =
        task {
            let context = invocation Api.context ()
            let! contextReply = transport.Exchange context token

            match unpack Api.context contextReply with
            | Some(Outcome.Succeeded scope) ->
                return! readCase newId console transport draft scope.InstallationId token
            | _ ->
                transport.Deliver context contextReply
                return contextReply.ExitCode
        }

    and private readCase newId console transport (draft: InteractiveCaseDraft) installation token =
        task {
            let lookup =
                invocation
                    Api.caseGet
                    {
                        CaseGetRequest.Target = CaseSelector.Reference draft.Reference
                    }

            let! response = transport.Exchange lookup token

            match unpack Api.caseGet response with
            | Some(Outcome.Succeeded current) ->
                let request =
                    {
                        OperationId = draft.SuppliedId |> Option.defaultWith newId
                        Target = CaseSelector.Id current.Snapshot.CaseId
                        ExpectedRevision = Revision.value current.Snapshot.Revision
                        Act = draft.Act
                    }

                return! review console transport draft request installation token
            | _ ->
                transport.Deliver lookup response
                return response.ExitCode
        }
