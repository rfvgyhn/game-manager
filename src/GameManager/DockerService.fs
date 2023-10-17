namespace Docker

open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Types

module Dummy =
    let api = {
        getContainers = fun _ -> task { return [ "asdf", Stopped ] |> Map.ofList |> Ok }
        getContainerState = fun _ -> task { return Ok Stopped }
        startContainer = fun _ -> task { return Ok Running }
    }
    
module Remote =
    open Docker.DotNet
    open Docker.DotNet.Models
    open System
    
    let private sendRequest fn = task {
        try
            return! fn                
        with
        | :? DockerApiException as e -> return Result.Error e.Message
        | :? TaskCanceledException -> return Result.Error "Docker API request timed out"
    }
    
    type private Health = Starting | Unhealthy | Healthy | None | Unknown of string
    let private mapState dockerState health =
        match dockerState, health with
        | "running", Starting -> ContainerState.Starting
        | "running", _
        | "created", _
        | "restarting", _ -> Running
        | "removing", _
        | "paused", _
        | "exited", _
        | "dead", _ -> Stopped
        | s, _ -> Error $"Unknown state '%s{s}'"
    
    let api (dockerClient: IDockerClient) (logger : ILogger) =
        let createParameters names =
            let filter = Dictionary<string, IDictionary<string, bool>>(
                            dict [ ("name", Dictionary<string, bool>(
                                                names |> List.map (fun n -> (n, true)) |> dict
                                            ) :> IDictionary<string, bool>) ]
                        )
            logger.LogDebug(sprintf "Filter: %s" (String.Join(": ", names)))
            let parameters = ContainersListParameters()
            parameters.All <- Nullable<bool>(true)
            parameters.Filters <- filter
            parameters
        let getHealth id = task {
            let! data = dockerClient.Containers.InspectContainerAsync(id)
            if data.State.Health = null then
                return None
            else
                return match data.State.Health.Status with
                       | "starting" -> Starting
                       | "healthy" -> Healthy
                       | "unhealthy" -> Unhealthy
                       | "none" -> None
                       | s -> Unknown s
        }            
        
        { getContainers = fun names -> 
              match names with
              | [] -> task { return Ok Map.empty }
              | _ -> task {
                  let parameters = createParameters names                  
                  let! containers = dockerClient.Containers.ListContainersAsync(parameters)
                  let getName (c : ContainerListResponse) = c.Names.[0].[1..] // Trim first char since docker prepends a '/'
                  logger.LogDebug(sprintf "Found Containers: %s" (String.Join(", ", containers |> Seq.map getName)))
                  let! healthMapResult =
                      containers
                      |> Seq.map (fun c -> task {
                          let! health = getHealth c.ID
                          return (c.ID, health)})
                      |> Task.WhenAll
                  let healthMap = healthMapResult |> dict
                  return containers
                         |> Seq.map (fun c -> getName c, mapState c.State healthMap.[c.ID])
                         |> Map.ofSeq
                         |> Ok
              }
          >> sendRequest
          
          getContainerState = fun name -> task {
              let parameters = createParameters [ name ]
              let! containers = dockerClient.Containers.ListContainersAsync(parameters)
              
              match containers |> List.ofSeq with
              | [] -> return Result.Error $"Couldn't find container named %s{name}"
              | c::[] ->
                  let! health = getHealth c.ID
                  return Ok <| mapState c.State health
              | _::_ -> return Result.Error $"Multiple containers found with filter name=%s{name}"
          } >> sendRequest
          
          startContainer = fun name -> task {
              match! dockerClient.Containers.StartContainerAsync(name, null) with
              | false -> return Result.Error "Unable to start container"    
              | true ->
                  let! health = getHealth name
                  return Ok <| mapState "running" health
          } >> sendRequest
        }
