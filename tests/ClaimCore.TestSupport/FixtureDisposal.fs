module internal ClaimCore.TestSupport.FixtureDisposal

open System
open System.Runtime.ExceptionServices

let private complete (initial: exn list) (steps: (unit -> unit) list) =
    let failures = ResizeArray<exn>(initial)

    for step in steps do
        try
            step ()
        with error ->
            failures.Add error

    match failures.Count with
    | 0 -> ()
    | 1 -> ExceptionDispatchInfo.Capture(failures[0]).Throw()
    | _ -> raise (AggregateException("Owned fixture failures.", failures))

let finish steps = complete [] steps

let afterFailure failure steps = complete [ failure ] steps
