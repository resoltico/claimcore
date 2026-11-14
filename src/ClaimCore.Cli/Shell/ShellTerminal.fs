namespace ClaimCore.Cli

open System
open System.Text
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type internal ShellLine =
    | Text of string
    | Interrupted of hadText: bool
    | End

module internal ShellKeys =
    let isEndOfInput isUnix isEmpty (key: ConsoleKeyInfo) =
        let control =
            (key.Modifiers &&& ConsoleModifiers.Control) <> enum<ConsoleModifiers> 0

        isEmpty
        && (key.KeyChar = '\u0004'
            || (key.Key = ConsoleKey.D && control)
            || (isUnix && key.Key = ConsoleKey.D2 && key.KeyChar = '\u0000' && control))

/// One input worker owns ReadKey throughout the shell, including a cancelled or idle prompt.
type internal ShellTerminal(write: string -> unit, maximumCharacters: int) =
    let gate = obj ()
    let requested = new SemaphoreSlim(0, 1)
    let history = ResizeArray<string>()
    let buffer = StringBuilder()
    let mutable pending: TaskCompletionSource<ShellLine> option = None
    let mutable remember = false
    let mutable position = 0
    let mutable draft = ""
    let mutable activePrompt = ""
    let mutable disposed = false

    let redraw () =
        write ("\r\u001b[2K" + activePrompt + TerminalText.data (buffer.ToString()))

    let complete value =
        let receiver = pending
        pending <- None
        receiver |> Option.iter (fun target -> target.TrySetResult value |> ignore)

    let recall delta =
        if position = history.Count then
            draft <- buffer.ToString()

        position <- Math.Clamp(position + delta, 0, history.Count)
        buffer.Clear() |> ignore

        buffer.Append(
            if position = history.Count then
                draft
            else
                history[position]
        )
        |> ignore

        redraw ()

    let accepted () =
        let line = buffer.ToString()

        if remember && not (String.IsNullOrWhiteSpace line) then
            if history.Count = 0 || history[history.Count - 1] <> line then
                history.Add line

            if history.Count > 100 then
                history.RemoveAt 0

        buffer.Clear() |> ignore
        write "\n"
        complete (ShellLine.Text line)

    let erase () =
        if buffer.Length > 0 then
            let last = buffer[buffer.Length - 1]
            buffer.Length <- buffer.Length - 1

            if Char.IsLowSurrogate last && buffer.Length > 0 then
                buffer.Length <- buffer.Length - 1

            redraw ()

    let append character =
        if buffer.Length < maximumCharacters then
            buffer.Append(character: char) |> ignore

            if Char.IsHighSurrogate character then
                ()
            elif
                Char.IsLowSurrogate character
                && buffer.Length > 1
                && Char.IsHighSurrogate buffer[buffer.Length - 2]
            then
                write (TerminalText.data (buffer.ToString(buffer.Length - 2, 2)))
            else
                write (TerminalText.data (string character))

    let key (key: ConsoleKeyInfo) =
        match key.Key with
        | ConsoleKey.Enter -> accepted ()
        | ConsoleKey.Backspace -> erase ()
        | ConsoleKey.Tab -> append '\t'
        | ConsoleKey.UpArrow when remember -> recall -1
        | ConsoleKey.DownArrow when remember -> recall 1
        | _ when ShellKeys.isEndOfInput (not (OperatingSystem.IsWindows())) (buffer.Length = 0) key ->
            write "\n"
            complete ShellLine.End
        | _ when not (Char.IsControl key.KeyChar) -> append key.KeyChar
        | _ -> ()

    let active () =
        lock gate (fun () -> not disposed && pending.IsSome)

    let worker () =
        try
            while not disposed do
                requested.Wait()

                while active () do
                    let received = Console.ReadKey true

                    lock gate (fun () ->
                        if pending.IsSome then
                            key received)
        with
        | :? InvalidOperationException
        | :? IO.IOException -> lock gate (fun () -> complete ShellLine.End)

    do Task.Run(Action worker) |> ignore

    member _.Read(keepHistory, prompt) =
        lock gate (fun () ->
            if pending.IsSome then
                invalidOp "Only one shell input read may be active."

            let completion =
                TaskCompletionSource<ShellLine>(TaskCreationOptions.RunContinuationsAsynchronously)

            pending <- Some completion
            remember <- keepHistory
            position <- history.Count
            draft <- ""
            buffer.Clear() |> ignore
            activePrompt <- prompt
            write prompt

            if requested.CurrentCount = 0 then
                requested.Release() |> ignore

            completion.Task)

    member _.Interrupt() =
        lock gate (fun () ->
            let hadText = buffer.Length > 0
            buffer.Clear() |> ignore

            try
                write "\n"
            with
            | :? IO.IOException
            | :? ObjectDisposedException -> ()

            complete (ShellLine.Interrupted hadText))

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                disposed <- true
                complete ShellLine.End
                history.Clear()
                buffer.Clear() |> ignore

                if requested.CurrentCount = 0 then
                    requested.Release() |> ignore)
