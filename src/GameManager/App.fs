module GameManager.App

open Docker.DotNet
open FSharp.Control
open Giraffe
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System
open System.Collections.Generic
open System.Linq
open System.Text.Json.Nodes
open System.Threading
open Types
    
let private getServerConfig (ctx: HttpContext) =
    ctx.GetService<AppConfig>().Servers
    
let private getClients (ctx: HttpContext) =
    ctx.GetService<IDockerClient>(), ctx.GetService<Azure.IAzureClient>()
    
let private getLogger (ctx: HttpContext) = ctx.GetLogger "GameManager"

let private buildRequest (ctx: HttpContext) server =
    let dockerClient, azureClient = getClients ctx
    let logger = getLogger ctx
    match server.Type with
    | ServerType.AzureVm c -> ServerHost.Request.AzureVm (logger, azureClient, c)
    | ServerType.Docker _ -> ServerHost.Request.Docker (logger, dockerClient, server.Id)

let private getServer id (ctx: HttpContext) =
    getServerConfig ctx
    |> List.tryFind (fun c -> c.Id = id)
    |> Option.map (fun server -> task {
        if not server.Enabled then
            return { server with State = Disabled }
        else
            let request = buildRequest ctx server
            match! ServerHost.getState ctx.RequestAborted request with
            | Ok s -> return { server with State = s }
            | Result.Error e -> return { server with State = Error e }
       })

let private getServers (ctx: HttpContext) = task {
    let serverStates = ctx.GetService<IStateTracker>().GetAllStates()
    
    return
        getServerConfig ctx
        |> List.map (fun s ->
            let state =
                serverStates
                |> Map.tryFind s.Id
                |> Option.defaultValue ServerState.Unknown
            { s with State = state })
        |> List.sortWith (fun s1 s2 -> compare (not s1.Enabled, s1.DisplayName) (not s2.Enabled, s2.DisplayName))
}

let private seeOther (location: string) : HttpHandler =
    setStatusCode StatusCodes.Status303SeeOther >=> setHttpHeader "Location" location

let private htmlFragment (view: unit -> XmlNode) : HttpFunc -> HttpContext -> HttpFuncResult =
    view() |> RenderView.AsString.htmlNode |> htmlString

let private fragmentOrRedirect (view: unit -> XmlNode) (location: string) : HttpHandler = fun next ctx ->
    if ctx.IsAjaxRequest() then
        htmlFragment view next ctx
    else
        seeOther location next ctx
        
let private fragmentOrError (view: unit -> XmlNode) (error: HttpFunc -> HttpContext -> HttpFuncResult) : HttpHandler = fun next ctx ->
    if ctx.IsAjaxRequest() then
        htmlFragment view next ctx
    else
        error next ctx

let private indexHandler =
    fun next ctx -> task {
        let! servers = getServers ctx
        
        let view = Views.index servers |> htmlView
            
        return! view next ctx
    }

let private startHandler name =
    fun next (ctx: HttpContext) -> task {        
        match getServer name ctx with
        | Some s ->
            let! server = s
            let err msg = fun () -> Views.tag server.Id (ServerState.Error msg)
            let tag state = fun () -> Views.tag server.Id state
            match server.State with
            | Error e -> return! fragmentOrError (err e) (ServerErrors.INTERNAL_ERROR e) next ctx
            | Stopped ->
                let request = buildRequest ctx server
                match! ServerHost.start ctx.RequestAborted request with
                | Ok state -> return! fragmentOrRedirect (tag state) "/" next ctx
                | Result.Error m ->
                    return! fragmentOrError (err m) (ServerErrors.INTERNAL_ERROR m) next ctx
            | state ->
                return! fragmentOrError (tag state) (RequestErrors.BAD_REQUEST "Can only start a stopped server") next ctx
        | None -> return! RequestErrors.NOT_FOUND "" next ctx
    }
    
// TypedResults.ServerSentEvents doesn't support sending comments so setup SSE manually
// https://github.com/dotnet/aspnetcore/issues/65103
let sseStatusHandler : HttpHandler = fun next ctx -> task {
    let statusService = ctx.GetService<IStateTracker>()
    let connectionTracker = ctx.GetService<IConnectionTracker>()
    let config = ctx.GetService<AppConfig>()
    let toMessage (s: ServerStatus) =
        let now = DateTimeOffset.UtcNow.ToIsoString()
        if s.State.Current.IsDisabled || s.State.Prev.IsDisabled then
            let server = { (config.Servers |> List.find (fun c -> c.Id = s.Id))  with State = s.State.Current }
            Views.card server
        else
            Views.tag s.Id s.State.Current
        |> RenderView.AsString.htmlNode
        |> (fun fragment -> $"id: %s{now}\ndata: %s{fragment}\n\n")
        
    let lastTimestamp =
        ctx.TryGetRequestHeader "Last-Event-ID"
        |> Option.bind (fun s ->
            match DateTimeOffset.TryParse(s) with
            | true, ts -> Some ts
            | _ -> None)
        |> Option.defaultValue DateTimeOffset.MinValue
    
    use cts = CancellationTokenSource.CreateLinkedTokenSource(
        ctx.RequestAborted, 
        ctx.GetService<IHostApplicationLifetime>().ApplicationStopping
    )
    let ct = cts.Token
    connectionTracker.Increment()
    try
        try
            use statuses = statusService.GetStatusStream lastTimestamp
            let stream =
                statuses :> IAsyncEnumerable<ServerStatus>
                |> TaskSeq.map toMessage
                |> AsyncEnumerable.merge ct (taskSeq {
                    let heartbeat = ": heartbeat\n\n"
                    use timer = new PeriodicTimer(config.SseHeartbeatInterval)
                    yield heartbeat
                    while! timer.WaitForNextTickAsync(ct) do
                        yield heartbeat
                })
            
            ctx.Response.ContentType <- "text/event-stream";
            ctx.Response.Headers.CacheControl <- "no-cache,no-store";
            ctx.Response.Headers.Pragma <- "no-cache";
            ctx.Response.Headers.ContentEncoding <- "identity";
            ctx.Features.GetRequiredFeature<IHttpResponseBodyFeature>().DisableBuffering();
            for message in stream do
                do! ctx.Response.WriteAsync(message, ct)
        with
        | :? OperationCanceledException -> ()
    finally
        connectionTracker.Decrement()
    return Some ctx
}

let private unauthorized (ctx: HttpContext) =
    ctx.SetStatusCode 401
    Some ctx

let private requireQueryValue description key (value: string option) : HttpHandler = fun next ctx -> task {
    let logger = getLogger ctx
    let isValid =
        match value with
        | Some s when not (String.IsNullOrWhiteSpace s) ->
            ctx.TryGetQueryStringValue key |> Option.contains s
        | _ ->
            logger.LogWarning($"%s{description} is empty. All events will be denied.")
            false

    if isValid then
        return! next ctx
    else
        return unauthorized ctx
}

// https://learn.microsoft.com/en-us/azure/event-grid/end-point-validation-event-grid-events-schema#validation-details
let private echoAegValidationCode : HttpHandler = fun next ctx -> task {
    match ctx.TryGetRequestHeader "aeg-event-type" with
    | Some "SubscriptionValidation" ->
        let (?>) = JsonNode.getValue
        let! events = ctx.BindJsonAsync<JsonArray>()
        let validationCode = events.FirstOrDefault() ?> "data" ?> "validationCode" |> JsonNode.asStr |> Option.defaultValue ""
        return! json {| validationResponse = validationCode |} next ctx
    | _ ->
        return! next ctx
}

let private devEnvBypass (innerHandler : HttpHandler) : HttpHandler = fun next ctx ->
    let env = ctx.GetService<IWebHostEnvironment>()
    if env.IsDevelopment() then
        (getLogger ctx).LogInformation("Dev environment detected. Bypassing authentication for {path}", ctx.Request.Path.Value)
        next ctx
    else
        innerHandler next ctx
    
let private azureStatusWebHook : HttpHandler = fun next ctx -> task {
    let logger = getLogger ctx
    let! events = ctx.BindJsonAsync<JsonArray>()
    
    events
    |> Seq.sortByDescending (fun n -> DateTimeOffset.Parse(n["eventTime"].ToJsonString()))
    |> Seq.head
    |> (fun node ->
        match node["subject"] |> JsonNode.asStr |> Option.defaultValue "" with
        | Azure.VmPath (subId, resGroup, vmName) ->
            let id = Azure.formatId resGroup vmName
            let isMonitored() =
                getServerConfig ctx |> List.exists (fun s -> s.Id = id && s.Enabled && s.StatusMode.IsPush)            
            
            if isMonitored() then
                let stateTracker = ctx.GetService<IStateTracker>()
                let event, timestamp = Azure.AzureEvent.parse node
                let state = stateTracker.GetState id
                let newState = Azure.AzureEvent.mapToState state.Prev event
                stateTracker.Notify id newState timestamp
            else
                logger.LogWarning("Received event for {ServerId} but it isn't configured for push events", id)
        | _ ->
            logger.LogWarning("Unexpected Azure event: {json}", node)
    )
    return! setStatusCode 200 next ctx
}

let private (|Prefix|_|) (prefix:string) (str:string) =
    if str.StartsWith(prefix) then
        Some(str.Substring(prefix.Length))
    else
        None
let private getUsername = function
    | Prefix "Basic " token ->
        token
        |> System.Convert.FromBase64String
        |> System.Text.ASCIIEncoding.ASCII.GetString
        |> String.split ':'
        |> Array.head
    | header -> header

let private logStartRequest next (ctx: HttpContext) =
    let logger = ctx.GetLogger "GameManager"
    let ipAddress = ctx.Connection.RemoteIpAddress
    let user =
        ctx.TryGetRequestHeader "Authorization"
        |> Option.orElseWith (fun () -> ctx.TryGetRequestHeader "Remote-User")
        |> Option.map getUsername
        |> Option.defaultValue "Unknown"
    logger.LogInformation $"User '%s{user}' from %A{ipAddress} requested to start server"
    next ctx
    
let webApp eventGridSharedSecret : HttpHandler =
    choose [
        GET  >=> route  "/" >=> indexHandler
        GET  >=> route  "/sse" >=> sseStatusHandler
        POST >=> routef "/servers/%s/start" (fun id -> logStartRequest >=> startHandler id)
        POST >=> route  "/webhooks/azure/eventgrid"
                 >=> requireQueryValue "EventGrid shared secret" "code" eventGridSharedSecret
                 >=> echoAegValidationCode
                 >=> (requiresAuthentication (challenge "Bearer") |> devEnvBypass)
                 >=> azureStatusWebHook
        RequestErrors.NOT_FOUND "Not Found"
    ]