namespace Docker

open System.Collections.Generic
open System.Threading.Tasks
open FSharp.Control.Tasks.ContextInsensitive
open Microsoft.Extensions.Logging
open Types

module Dummy =
    let api = {
        getContainers = fun names -> task { return [ "asdf", Stopped ] |> Map.ofList |> Ok }
        getContainerState = fun id -> task { return Ok Stopped }
        startContainer = fun id -> task { return Ok Running }
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
        
    let private mapState dockerState =
        match dockerState with
        | "created"
        | "restarting"
        | "running" -> Running
        | "removing"
        | "paused"
        | "exited"
        | "dead" -> Stopped
        | _ -> Error "Unknown state"
    
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
        
        { getContainers = fun names -> 
              match names with
              | [] -> task { return Ok Map.empty }
              | _ -> task {
                  let parameters = createParameters names                  
                  let! containers = dockerClient.Containers.ListContainersAsync(parameters)
                  let getName (c : ContainerListResponse) = c.Names.[0].[1..] // Trim first char since docker prepends a '/'
                  logger.LogDebug(sprintf "Found Containers: %s" (String.Join(", ", containers |> Seq.map getName)))
                  
                  return containers
                         |> Seq.map (fun c -> getName c, mapState c.State )
                         |> Map.ofSeq
                         |> Ok
              }
          >> sendRequest
          getContainerState = fun name -> task {
              let parameters = createParameters [ name ]
              let! containers = dockerClient.Containers.ListContainersAsync(parameters)
              
              match containers |> List.ofSeq with
              | [] -> return Result.Error <| sprintf "Couldn't find container named %s" name
              | c::[] -> return Ok <| mapState c.State
              | c::tail -> return Result.Error <| sprintf "Multiple containers found with filter name=%s" name
          } >> sendRequest
          
          startContainer = fun name -> task {
              match! dockerClient.Containers.StartContainerAsync(name, null) with
              | true -> return Ok Running
              | false -> return Result.Error "Unable to start container"         
          } >> sendRequest
        }
