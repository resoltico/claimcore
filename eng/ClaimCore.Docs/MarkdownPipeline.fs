namespace ClaimCore.Docs

open Markdig
open Markdig.Extensions.AutoIdentifiers

[<RequireQualifiedAccess>]
module MarkdownPipeline =
    let value =
        MarkdownPipelineBuilder()
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
            .UsePreciseSourceLocation()
            .Build()
