module internal ClaimCore.TestSupport.IdentityReadiness

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks

let private refuse category =
    // Closed status/category values only: never provider bodies, addresses or exception text.
    Console.WriteLine("Owned identity readiness refused: " + category)
    invalidOp ("Owned identity readiness refused: " + category)

let private probe (client: HttpClient) (location: Uri) validate (ct: CancellationToken) =
    task {
        try
            use! response = client.GetAsync(location, ct)
            let status = "HTTP_" + string (int response.StatusCode)

            match response.StatusCode with
            | HttpStatusCode.NotFound
            | HttpStatusCode.ServiceUnavailable -> return false, status
            | _ when not response.IsSuccessStatusCode -> return refuse status
            | _ ->
                let! bytes = response.Content.ReadAsByteArrayAsync ct

                if validate (ReadOnlyMemory bytes) then
                    return true, "READY"
                else
                    return refuse "METADATA_VALIDATION"
        with
        | :? HttpRequestException as error when
            error.HttpRequestError = HttpRequestError.SecureConnectionError
            ->
            return refuse "TLS_VALIDATION"
        | :? HttpRequestException as error ->
            return false, "TRANSPORT_" + string error.HttpRequestError
        | :? OperationCanceledException when not ct.IsCancellationRequested ->
            return false, "REQUEST_TIMEOUT"
    }

let wait (client: HttpClient) location validate (ct: CancellationToken) =
    task {
        use deadline = CancellationTokenSource.CreateLinkedTokenSource ct
        deadline.CancelAfter(TimeSpan.FromSeconds 30.)
        let mutable ready = false
        let mutable observed = "NO_RESPONSE"

        try
            while not ready do
                let! current, category = probe client location validate deadline.Token
                ready <- current

                if category <> observed then
                    Console.WriteLine("Owned identity readiness: " + category)
                    observed <- category

                if not ready then
                    do! Task.Delay(200, deadline.Token)
        with
        | :? OperationCanceledException when ct.IsCancellationRequested ->
            return raise (OperationCanceledException ct)
        | :? OperationCanceledException -> return refuse ("DEADLINE_" + observed)
    }
