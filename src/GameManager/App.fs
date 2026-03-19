module GameManager.App

open Azure.ResourceManager
open Docker.DotNet
open FSharp.Control
open Giraffe
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System
open System.Collections.Generic
open System.Threading
open Types
    
let private getServerConfig (ctx: HttpContext) =
    ctx.GetService<AppConfig>().Servers
    
let private getClients (ctx: HttpContext) =
    ctx.GetService<IDockerClient>(), ctx.GetService<ArmClient>()
    
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

let private fragmentOrRedirect (view: unit -> XmlNode) (location: string) : HttpHandler = fun next ctx ->
    if ctx.IsAjaxRequest() then
        htmlView (view()) next ctx
    else
        seeOther location next ctx
        
let private fragmentOrError (view: unit -> XmlNode) (error: HttpFunc -> HttpContext -> HttpFuncResult) : HttpHandler = fun next ctx ->
    if ctx.IsAjaxRequest() then
        htmlView (view()) next ctx
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
            match server.State with
            | Error e -> return! fragmentOrError (err e) (ServerErrors.INTERNAL_ERROR e) next ctx
            | Stopped ->
                let request = buildRequest ctx server
                match! ServerHost.start ctx.RequestAborted request with
                | Ok state -> return! fragmentOrRedirect (fun () -> Views.tag server.Id state) "/" next ctx
                | Result.Error m ->
                    return! fragmentOrError (err m) (ServerErrors.INTERNAL_ERROR m) next ctx
            | _ ->
                return! RequestErrors.BAD_REQUEST "Can only start a stopped server" next ctx
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
    
let webApp : HttpHandler =
    choose [
        GET  >=> route  "/" >=> indexHandler
        GET  >=> route  "/sse" >=> sseStatusHandler
        POST >=> routef "/servers/%s/start" (fun id -> logStartRequest >=> startHandler id)
        RequestErrors.NOT_FOUND "Not Found"
    ]