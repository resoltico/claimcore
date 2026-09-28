module ClaimCore.WebTests.OidcLiveBrowser

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks

let private value (root: JsonElement) (name: string) =
    root.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> invalidOp "Synthetic OIDC credential field is missing.")

let private cookies (response: HttpResponseMessage) =
    match response.Headers.TryGetValues("Set-Cookie") with
    | true, entries ->
        entries
        |> Option.ofObj
        |> Option.map (Seq.map (fun entry -> entry.Split(';', 2)[0]) >> Seq.toList)
        |> Option.defaultValue []
    | false, _ -> []

let requireStatus expected (response: HttpResponseMessage) =
    if int response.StatusCode <> expected then
        invalidOp
            $"Synthetic OIDC HTTP step returned {int response.StatusCode}, expected {expected}."

let private location (response: HttpResponseMessage) =
    response.Headers.Location
    |> Option.ofObj
    |> Option.defaultWith (fun () -> invalidOp "Synthetic OIDC redirect is missing.")

let private formPost (html: string) =
    let decoded source =
        WebUtility.HtmlDecode(source) |> Option.ofObj |> Option.defaultValue ""

    let form =
        Regex.Match(html, "<form[^>]*action=\"(?<url>[^\"]+)\"", RegexOptions.IgnoreCase)

    if not form.Success then
        invalidOp "Synthetic OIDC form-post callback is missing."

    let fields =
        Regex.Matches(html, "<input[^>]*>", RegexOptions.IgnoreCase)
        |> Seq.cast<Match>
        |> Seq.choose (fun input ->
            let attribute name =
                let found =
                    Regex.Match(
                        input.Value,
                        name + "=\"(?<value>[^\"]*)\"",
                        RegexOptions.IgnoreCase
                    )

                if found.Success then
                    Some(decoded (found.Groups["value"].Value))
                else
                    None

            match attribute "name", attribute "value" with
            | Some name, Some value when
                [ "code"; "state"; "session_state"; "iss" ] |> List.contains name
                ->
                Some(KeyValuePair(name, value))
            | _ -> None)
        |> Seq.toList

    if fields |> List.exists (fun field -> field.Key = "code") |> not then
        invalidOp "Synthetic OIDC callback code is missing."

    Uri(decoded (form.Groups["url"].Value)), fields

let private loginPage (client: HttpClient) (start: Uri) =
    let rec follow (current: Uri) remaining =
        task {
            if remaining = 0 then
                invalidOp "Synthetic OIDC login redirect limit exceeded."

            use! response = client.GetAsync(current)

            if int response.StatusCode = 302 then
                return! follow (Uri(current, location response)) (remaining - 1)
            else
                requireStatus 200 response
                let! html = response.Content.ReadAsStringAsync()
                let form = Regex.Match(html, "action=\"(?<url>[^\"]+)\"")

                if not form.Success then
                    invalidOp "Synthetic OIDC login form is missing."

                let action = WebUtility.HtmlDecode(form.Groups["url"].Value)
                return Uri(current, action), cookies response
        }

    follow start 8

let private postCredentials (client: HttpClient) (action: Uri) sessionCookies (user: JsonElement) =
    task {
        use fields =
            new FormUrlEncodedContent(
                [
                    KeyValuePair("username", value user "username")
                    KeyValuePair("password", value user "password")
                    KeyValuePair("credentialId", "")
                ]
            )

        use request = new HttpRequestMessage(HttpMethod.Post, action)

        request.Headers.TryAddWithoutValidation("Cookie", String.concat "; " sessionCookies)
        |> ignore

        request.Content <- fields
        return! client.SendAsync(request)
    }

let private callbackFrom (response: HttpResponseMessage) =
    task {
        if int response.StatusCode = 302 then
            return location response, None
        else
            requireStatus 200 response
            let! html = response.Content.ReadAsStringAsync()
            let uri, fields = formPost html
            return uri, Some fields
    }

let private completeCallback
    (local: HttpClient)
    (callback: Uri)
    (fields: KeyValuePair<string, string> list option)
    correlationCookies
    (stage: string -> unit)
    =
    task {
        if callback.Host <> "localhost" || callback.AbsolutePath <> "/signin-oidc" then
            invalidOp "Synthetic OIDC returned an unexpected callback."

        use request =
            new HttpRequestMessage(
                (if fields.IsSome then HttpMethod.Post else HttpMethod.Get),
                callback.PathAndQuery
            )

        fields
        |> Option.iter (fun values -> request.Content <- new FormUrlEncodedContent(values))

        request.Headers.TryAddWithoutValidation("Cookie", String.concat "; " correlationCookies)
        |> ignore

        stage "callback"
        use! response = local.SendAsync(request)
        requireStatus 302 response
        let sessionCookies = cookies response

        if sessionCookies.IsEmpty then
            invalidOp "Synthetic OIDC session cookie is missing."

        use session = new HttpRequestMessage(HttpMethod.Get, "/auth/state")

        session.Headers.TryAddWithoutValidation("Cookie", String.concat "; " sessionCookies)
        |> ignore

        stage "session"
        use! state = local.SendAsync(session)
        requireStatus 204 state
        return sessionCookies
    }

let login (local: HttpClient) (identity: HttpClient) (credentials: JsonElement) stage =
    task {
        stage "challenge"
        use! challenge = local.GetAsync("/auth/login")
        requireStatus 302 challenge
        let authorize = location challenge

        if
            not (authorize.Query.Contains("code_challenge_method=S256", StringComparison.Ordinal))
        then
            invalidOp "Synthetic OIDC challenge did not use S256 PKCE."

        stage "login-form"
        let! action, keycloakCookies = loginPage identity authorize
        stage "credentials"

        use! accepted =
            postCredentials identity action keycloakCookies (credentials.GetProperty("users")[0])

        let! callback, fields = callbackFrom accepted
        return! completeCallback local callback fields (cookies challenge) stage
    }
