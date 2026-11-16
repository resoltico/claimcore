namespace ClaimCore.Domain

open System
open System.Text.RegularExpressions

/// A task token is an identity; registry membership is resolved independently.
[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type TaskId = private TaskId of string

module TaskId =
    let token (TaskId value) = value

    let tryParse value =
        if
            not (String.IsNullOrEmpty value)
            && Regex.IsMatch(value, @"\A[a-z]+(\.[a-z][A-Za-z]*)+\z", RegexOptions.CultureInvariant)
        then
            Some(TaskId value)
        else
            None

    let ofLiteral value =
        match tryParse value with
        | Some task -> task
        | None -> invalidArg (nameof value) "Use a dotted task token."
