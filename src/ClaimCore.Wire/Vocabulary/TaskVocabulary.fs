namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Domain.Tasks

module TaskVocabulary =
    let all = Registry.all |> List.map (Task.declaration >> _.Id)
    let client = Registry.clientDeclarations |> List.map _.Id
    let vocabulary = Vocabulary.define "task" all TaskId.token
    let codec = VocabularyCodec.create vocabulary

    let forSubject subject =
        VocabularyCodec.select
            (fun id ->
                Registry.findDeclaration id
                |> Option.exists (fun declaration -> declaration.Target = subject))
            vocabulary

    let count subject =
        Registry.all
        |> List.filter (Task.declaration >> fun declaration -> declaration.Target = subject)
        |> List.length
