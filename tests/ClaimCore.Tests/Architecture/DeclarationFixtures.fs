namespace ClaimCore.Tests.Architecture.DeclarationFixtures

open ClaimCore.Domain

/// The fixture is compiled but deliberately has no other fixture-type consumer.
type Unreferenced = { UnusedValue: int }

type Referenced = { UsedValue: int }

module Consumer =
    let read () : Referenced = { UsedValue = 1 }

[<RequireQualifiedAccess>]
type CaseSample =
    | Used
    | Unused

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseSample =
    let vocabulary = Vocabulary.ofUnion<CaseSample> "fixture" TokenCasing.CamelCase None

module CaseConsumer =
    let read value =
        match value with
        | CaseSample.Used -> true
        | _ -> false

[<RequireQualifiedAccess>]
type DuplicateFirst =
    | Empty
    | Value of string

[<RequireQualifiedAccess>]
type DuplicateSecond =
    | Empty
    | Value of string

[<RequireQualifiedAccess>]
type Distinct =
    | Empty
    | Value of int

type DuplicateRecordFirst<'value> = { Item: 'value; Count: int }
type DuplicateRecordSecond<'item> = { Item: 'item; Count: int }
type DistinctRecord<'value> = { Item: 'value; Total: int }
