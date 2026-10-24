module ClaimCore.OutputProbe

open System
open System.IO
open System.Text
open ClaimCore.Cli
open Microsoft.Win32.SafeHandles

let private payload = Encoding.UTF8.GetBytes("Rīga مطالبة 😀\n")

let private zeroOffset () =
    use handle = new SafeFileHandle(IntPtr 1, ownsHandle = false)

    use stream =
        new FileStream(handle, FileAccess.Write, bufferSize = 1, isAsync = false)

    if stream.Position <> 0L then
        invalidOp "The append control must start at offset zero."

let private write methodName =
    use errors = StandardSinks.errors ()

    if methodName = "file-failure" then
        errors.Write(Encoding.UTF8.GetBytes "STREAM_READY\n")
        errors.Flush()

        if Console.OpenStandardInput().ReadByte() <> 42 then
            invalidOp "The file-failure control was not admitted."

    use output =
        if methodName = "console" then
            Console.OpenStandardOutput()
        else
            StandardSinks.output ()

    output.Write payload

    if methodName = "twice" then
        output.Write payload

    output.Flush()
    output.Dispose()

    if methodName = "lifetime" then
        for _ in 1..200 do
            use borrowed = StandardSinks.output ()
            borrowed.Flush()

        use borrowed = StandardSinks.output ()
        borrowed.Write payload
        borrowed.Flush()

    let marker =
        if methodName = "both" then
            "STREAM_SECOND_WRITER\n"
        else
            "STREAM_WRITE_RETURNED\n"

    errors.Write(Encoding.UTF8.GetBytes marker)
    errors.Flush()

[<EntryPoint>]
let main arguments =
    try
        if Array.contains "zero-offset" arguments then
            zeroOffset ()

        write arguments[0]
        0
    with
    | :? IOException -> 70
    | _ -> 71
