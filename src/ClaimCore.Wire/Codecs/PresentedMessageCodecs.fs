namespace ClaimCore.Wire

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Presentation

module PresentedMessageCodecs =
    open Codec
    open ResponseScalars
    open PresentationKeys
    open PresentedValueCodecs

    let private messageArguments =
        record (fun key value -> key, value)
        |> field "key" fst key
        |> field "value" snd value
        |> build
        |> named "PresentedArgument"
        |> list MaximumEntries
        |> fun codec -> restrict codec.Schema PresentationStructure.arguments codec
        |> map Map.ofList Map.toList

    let message =
        (record (fun message arguments ->
            {
                PresentedMessage.MessageId = message
                Arguments = arguments
            })
        : Fields<PresentedMessage, _>)
        |> field "messageId" _.MessageId messageId
        |> field "arguments" _.Arguments messageArguments
        |> build
        |> named "PresentedMessage"

    let advice =
        record (fun message action ->
            {
                HumanAdvice.Message = message
                Action = action
            })
        |> field "message" _.Message message
        |> field "action" _.Action (VocabularyCodec.create RecommendedAction.vocabulary)
        |> build
        |> named "HumanAdvice"
