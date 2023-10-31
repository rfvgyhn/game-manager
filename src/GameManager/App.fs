module GameManager.App

open Azure.ResourceManager
open Docker.DotNet
open Microsoft.AspNetCore.Http
open Giraffe
open Microsoft.Extensions.Logging
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
    let dockerClient, azureClient = getClients ctx
    let logger = getLogger ctx
    let enabledServers = getServerConfig ctx |> List.filter (fun s -> s.Enabled)
    let! serverStates = ServerHost.getStates ctx.RequestAborted {
         Logger = logger
         AzureClient = azureClient
         DockerClient = dockerClient
         Servers = enabledServers } 
    
    return
        getServerConfig ctx
        |> List.map (fun s ->
            let state =
                if not s.Enabled then Disabled
                else serverStates
                     |> Map.tryFind(s)
                     |> Option.map (Result.defaultWith ServerState.Error)
                     |> Option.defaultValue ServerState.Unknown
            { s with State = state })
}

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
            match server.State with
            | Error e -> return! ServerErrors.INTERNAL_ERROR e next ctx
            | Running | Starting | Disabled | Stopping | Fetching | ServerState.Unknown ->
                return! RequestErrors.BAD_REQUEST "Can only start a stopped server" next ctx
            | Stopped ->
                let request = buildRequest ctx server
                match! ServerHost.start ctx.RequestAborted request with
                | Ok state -> return! htmlView (Views.card { server with State = state }) next ctx
                | Result.Error m -> return! ServerErrors.INTERNAL_ERROR m next ctx
        | None -> return! RequestErrors.NOT_FOUND "" next ctx
    }

let private statusHandler id =
    fun next (ctx: HttpContext) -> task {
        match getServer id ctx with
        | Some s ->
            let! server = s
            match server.State with
            | Error e ->
                return! ServerErrors.INTERNAL_ERROR e next ctx
            | Running | Starting | Disabled | Stopped | Stopping | Fetching | ServerState.Unknown ->
                return! htmlView (Views.card server) next ctx
        | None -> return! RequestErrors.NOT_FOUND "" next ctx
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
        GET  >=> routef "/servers/%s" statusHandler
        POST >=> logStartRequest >=>
                 routef "/servers/%s/start" startHandler
        RequestErrors.NOT_FOUND "Not Found"
    ]