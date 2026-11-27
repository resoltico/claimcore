namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

/// Complete operational requests, with closed command/task associations and fallible principals.
module internal CommandControlCodecs =
    open Codec

    let private principal kind maximumSubject =
        record (fun issuer subject -> PrincipalKey.ofKind kind issuer subject)
        |> fixedField "command" (CommandTaskId.key CommandTaskId.DismissAccessRequest)
        |> fixedField "input:principal.kind" (Vocabulary.token PrincipalKind.vocabulary kind)
        |> field "input:principal.issuer" (PrincipalKey.toInput >> _.Issuer) PrincipalCodecs.issuer
        |> field
            "input:principal.subject"
            (PrincipalKey.toInput >> _.Subject)
            (boundedText 1 maximumSubject)
        |> buildResult (fun path _ ->
            {
                Path = path
                Reason = "Invalid principal representation."
            })

    let forTask =
        function
        | CommandTaskId.RequestAccess ->
            record ()
            |> fixedField "command" (CommandTaskId.key CommandTaskId.RequestAccess)
            |> build
            |> map (fun () -> CommandRequest.RequestAccess) (function
                | CommandRequest.RequestAccess -> ()
                | _ -> invalidOp "Access request command required.")
        | CommandTaskId.DismissAccessRequest ->
            let alternative kind maximumSubject =
                branch
                    CommandRequest.DismissAccessRequest
                    (function
                    | CommandRequest.DismissAccessRequest value when
                        (PrincipalKey.toInput value).Kind = kind
                        ->
                        Some value
                    | _ -> None)
                    (principal kind maximumSubject)

            choice
                "DismissAccessRequestControl"
                [
                    alternative PrincipalKind.Human PrincipalKey.MaximumHumanSubjectCharacters
                    alternative PrincipalKind.Service PrincipalKey.MaximumServiceSubjectCharacters
                ]
        | CommandTaskId.TerminateOwnSessions ->
            record ()
            |> fixedField "command" (CommandTaskId.key CommandTaskId.TerminateOwnSessions)
            |> build
            |> map (fun () -> CommandRequest.TerminateOwnSessions) (function
                | CommandRequest.TerminateOwnSessions -> ()
                | _ -> invalidOp "Own-session command required.")
        | CommandTaskId.TerminatePersonSessions ->
            record id
            |> fixedField "command" (CommandTaskId.key CommandTaskId.TerminatePersonSessions)
            |> field
                (TaskInputId.token TaskInputId.ActorId)
                id
                (uuid |> map (ActorId.create >> Option.get) ActorId.value)
            |> build
            |> map CommandRequest.TerminatePersonSessions (function
                | CommandRequest.TerminatePersonSessions value -> value
                | _ -> invalidOp "Person-session command required.")
