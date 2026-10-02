namespace ClaimCore.Database

open System
open System.Globalization
open System.IO
open System.Net.Sockets
open System.Threading

/// Private inherited duplex descriptor. Neither connection material nor frame bytes use argv,
/// stdout, stderr, or a caller-selected executable.
module internal DatabaseBackupControlPipe =
    let private descriptor () =
        let encoded = Environment.GetEnvironmentVariable("CLAIMCORE_BACKUP_CONTROL_FD")
        let mutable value = 0

        if
            OperatingSystem.IsWindows()
            || String.IsNullOrWhiteSpace encoded
            || not (
                Int32.TryParse(encoded, NumberStyles.None, CultureInfo.InvariantCulture, &value)
            )
            || value < 3
            || value > 1024
        then
            invalidOp "Private backup control descriptor is unavailable."

        value

    let openDuplex () =
        let handle = new SafeSocketHandle(nativeint (descriptor ()), ownsHandle = true)

        let socket =
            try
                new Socket(handle)
            with _ ->
                handle.Dispose()
                reraise ()

        try
            socket.ReceiveTimeout <- 30000
            socket.SendTimeout <- 30000
            new NetworkStream(socket, ownsSocket = true) :> Stream
        with _ ->
            socket.Dispose()
            reraise ()

    let allowCaptureUntil (stream: Stream) (expiresAt: DateTimeOffset) (now: DateTimeOffset) =
        let remaining = expiresAt - now

        if
            not stream.CanTimeout
            || remaining <= TimeSpan.Zero
            || remaining > TimeSpan.FromMinutes(30.)
        then
            invalidOp "Private backup lease timeout is invalid."

        stream.ReadTimeout <- int (min remaining.TotalMilliseconds (float Int32.MaxValue))

    let private readFrameAsync (stream: Stream) cancelled =
        task {
            let buffer = ResizeArray<byte>()
            let one = Array.zeroCreate<byte> 1
            let mutable complete = false
            let mutable exhausted = false

            while not complete && not exhausted do
                let! received = stream.ReadAsync(one.AsMemory(), cancelled)

                if received = 0 then
                    exhausted <- true
                elif buffer.Count >= 16384 then
                    invalidOp "Private backup frame exceeds its bound."
                else
                    buffer.Add(one[0])
                    complete <- one[0] = byte '\n'

            if complete then
                return Some(buffer.ToArray())
            elif buffer.Count = 0 then
                return None
            else
                return invalidOp "Private backup frame was interrupted."
        }

    let readFrameWithCancellation (stream: Stream) (cancelled: CancellationToken) =
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelled)

        if stream.CanTimeout && stream.ReadTimeout > 0 then
            deadline.CancelAfter(stream.ReadTimeout)

        readFrameAsync stream deadline.Token
        |> fun work -> work.GetAwaiter().GetResult()

    let readFrame stream =
        readFrameWithCancellation stream CancellationToken.None

    let writeFrame (stream: Stream) (bytes: byte array) =
        if
            isNull (box bytes)
            || bytes.Length < 2
            || bytes.Length > 16384
            || bytes[bytes.Length - 1] <> byte '\n'
        then
            invalidOp "Private backup response is invalid."

        stream.Write(bytes, 0, bytes.Length)
        stream.Flush()
