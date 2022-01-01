module GameManager.App

open Microsoft.AspNetCore.Http
open Giraffe
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging
open Types

let private getConfig<'T> (ctx: HttpContext) =
    ctx.GetService<IOptionsMonitor<'T>>().CurrentValue
    
let private getContainerConfig (ctx: HttpContext) =
    (getConfig<ContainerConfig> ctx).Containers |> List.ofSeq
    
let private getLogger (ctx: HttpContext) = ctx.GetLogger "GameManager"
    
let private getContainer name dockerApi (ctx: HttpContext) =
    getContainerConfig ctx
    |> List.tryFind (fun c -> c.Name = name)
    |> Option.map (fun c -> task {
            if not c.Enabled then
                return { c with State = Disabled }
            else
                let! state = dockerApi.getContainerState c.Name
                match state with
                | Ok s -> return { c with State = s }
                | Result.Error e -> return { c with State = Error e }
       })
    |> Option.defaultValue (task { return Container.Unknown })

let private getContainers dockerApi ctx = task {
    let enabledContainersNames =
        getContainerConfig ctx
        |> List.filter (fun c -> c.Enabled)
        |> List.map (fun c -> c.Name)
    
    let! remoteContainers = dockerApi.getContainers enabledContainersNames
        
    return
        match remoteContainers with
        | Result.Error m -> Result.Error m
        | Ok remote ->
           getContainerConfig ctx
           |> List.map (fun c ->
               let state =
                    if not c.Enabled then Disabled
                    else remote
                         |> Map.tryFind(c.Name)
                         |> Option.defaultValue Unknown
               { c with State = state })
           |> Ok
}

let private indexHandler dockerApi =
    fun next ctx -> task {
        let! containers = getContainers dockerApi ctx
        
        let view =
            match containers with
            | Result.Error m ->
                (getLogger ctx).LogError(m)
                text "Error getting containers"
            | Ok c -> Views.index c |> htmlView 
            
        return! view next ctx
    }

let private startHandler dockerApi name =
    fun next (ctx: HttpContext) -> task {
        let! container = getContainer name dockerApi ctx
        
        let! response =
            match container.State with
            | Error e ->
                task { return ServerErrors.INTERNAL_ERROR e }
            | Running | Starting | Disabled | Unknown ->
                task { return RequestErrors.BAD_REQUEST "Can only start a stopped container" }
            | Stopped ->
                task {
                    match! dockerApi.startContainer container.Name with
                    | Ok state -> return htmlView (Views.card { container with State = state })
                    | Result.Error m -> return ServerErrors.INTERNAL_ERROR m
                }
                
        return! response next ctx
    }

let private statusHandler dockerApi name =
    fun next (ctx: HttpContext) -> task {
        let! container = getContainer name dockerApi ctx
        let response =
            match container.State with
            | Error e ->
                ServerErrors.INTERNAL_ERROR e
            | Running | Starting | Disabled | Unknown | Stopped ->
                htmlView (Views.card container)
                
        return! response next ctx
    }

let private (|Prefix|_|) (prefix:string) (str:string) =
        if str.StartsWith(prefix) then
            Some(str.Substring(prefix.Length))
        else
            None
let private splitStr (seperator:char) (str:string) = str.Split(seperator)
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
    logger.LogInformation <| sprintf "User '%s' from %A requested to start container" user ipAddress
    next ctx
    
let webApp dockerApi : HttpHandler =
    choose [
        GET  >=> route  "/" >=> indexHandler dockerApi
        GET  >=> routef "/containers/%s" (statusHandler dockerApi)
        POST >=> logStartRequest >=>
                 routef "/containers/%s/start" (startHandler dockerApi)
        RequestErrors.NOT_FOUND "Not Found"
    ]