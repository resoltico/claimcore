namespace ClaimCore.Cli

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
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

    let private draft arguments (prepared: PreparedVerb) =
        let frame =
            Frames.admit prepared.Bytes
            |> Result.defaultWith (fun _ ->
                invalidOp "A prepared draft must have an admitted shape.")

        match RequestCodecs.operationInput.Read "$" (Frames.input frame) with
        | Ok(OperationInput.Case request) ->
            {
                Reference = request.CaseReference
                Act = request.Act
                SuppliedId =
                    if hasOption "--operation-id" arguments then
                        Some request.OperationId
                    else
                        None
                DisplayLanguage = prepared.Language
                PreviewOnly = hasOption "--preview" arguments
            }
        | _ -> invalidOp "An existing-case draft must carry a case act."

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
                // These shape-only values never enter the draft or a dispatched invocation.
                let shapeId () = "10000000-0000-4000-8000-000000000001"

                Grammar.prepare
                    shapeId
                    (keepGroups entry arguments @ [ "--expected-revision"; "0" ])
                |> Result.map (draft arguments >> Some)

    let private invocation (endpoint: Endpoint<'Input, 'Output>) input =
        JsonValue.Object
            [
                "endpoint", JsonValue.String endpoint.Token
                "input", endpoint.Input.Write input
            ]
        |> WireWriter.bytes
        |> Frames.admit
        |> Result.defaultWith (fun _ ->
            invalidOp "A typed review step must produce an admitted invocation.")

    let private unpack (endpoint: Endpoint<'Input, 'Output>) (reply: CliReply) =
        match reply.Frame.Result with
        | FrameResult.Reply value when value.Route.Id = endpoint.Id ->
            endpoint.Output.Read "$" value.Wire
            |> Result.defaultWith (fun _ ->
                invalidOp "An admitted review response must retain its endpoint shape.")
            |> Some
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
        (request: UnvalidatedCaseRequest)
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
                    let execute = invocation Api.operationExecute input
                    let! accepted = transport.Exchange execute token
                    transport.Deliver execute accepted
                    return accepted.ExitCode
                else
                    return ExitCode.value ExitCode.Success
            | _ -> return response.ExitCode
        }

    let run newId console transport (draft: InteractiveCaseDraft) token =
        task {
            let lookup =
                invocation
                    Api.caseGet
                    {
                        CaseGetRequest.CaseReference = draft.Reference
                    }

            let! response = transport.Exchange lookup token

            match unpack Api.caseGet response with
            | Some(Outcome.Succeeded current) ->
                let request =
                    {
                        OperationId = draft.SuppliedId |> Option.defaultWith newId
                        CaseReference = draft.Reference
                        ExpectedRevision = Revision.value current.Snapshot.Revision
                        Act = draft.Act
                    }

                return! review console transport draft request token
            | _ ->
                transport.Deliver lookup response
                return response.ExitCode
        }
