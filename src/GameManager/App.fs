module GameManager.App

open Docker.DotNet
open Microsoft.AspNetCore.Http
open Giraffe
open Microsoft.Extensions.Logging
open Types
    
let private getServerConfig (ctx: HttpContext) =
    ctx.GetService<AppConfig>().Servers
    
let private getLogger (ctx: HttpContext) = ctx.GetLogger "GameManager"

let private makeRequest (ctx: HttpContext) server =
    let dockerApi = ctx.GetService<IDockerClient>()
    let logger = getLogger ctx
    match server.Type with
    | ServerType.AzureVm _ -> Server.Request.AzureVm (logger, dockerApi, server.Id)
    | ServerType.Docker _ -> Server.Request.Docker (logger, dockerApi, server.Id)

let private getServer id (ctx: HttpContext) =
    getServerConfig ctx
    |> List.tryFind (fun c -> c.Id = id)
    |> Option.map (fun server -> task {
        if not server.Enabled then
            return { server with State = Disabled }
        else
            let request = makeRequest ctx server
            match! Server.getState request with
            | Ok s -> return { server with State = s }
            | Result.Error e -> return { server with State = Error e }
       })

let private getServers (ctx: HttpContext) = task {
    let dockerClient = ctx.GetService<IDockerClient>()
    let logger = getLogger ctx
    let enabledServerNames =
        getServerConfig ctx
        |> List.filter (fun c -> c.Enabled)
        |> List.map (fun c -> c.Id)
    
    let! serverStates = Server.getStates { Logger = logger; AzureClient = dockerClient, []; DockerClient = dockerClient, enabledServerNames }
    
    return
        getServerConfig ctx
        |> List.map (fun c ->
            let state =
                if not c.Enabled then Disabled
                else serverStates
                     |> Map.tryFind(c.Id)
                     |> Option.map (Result.defaultWith ServerState.Error)
                     |> Option.defaultValue ServerState.Unknown
            { c with State = state })
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
            | Running | Starting | Disabled | ServerState.Unknown ->
                return! RequestErrors.BAD_REQUEST "Can only start a stopped server" next ctx
            | Stopped ->
                let request = makeRequest ctx server
                match! Server.start request with
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
            | Running | Starting | Disabled | ServerState.Unknown | Stopped ->
                return! htmlView (Views.card server) next ctx
        | None -> return! RequestErrors.NOT_FOUND "" next ctx
    }

let private (|Prefix|_|) (prefix:string) (str:string) =
        if str.StartsWith(prefix) then
            Some(str.Substring(prefix.Length))
        else
            None
let private splitStr (separator:char) (str:string) = str.Split(separator)
let private getUsername = function
    | Prefix "Basic " token ->
        token
        |> System.Convert.FromBase64String
        |> System.Text.ASCIIEncoding.ASCII.GetString
        |> splitStr ':'
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