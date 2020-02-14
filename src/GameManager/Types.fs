module Types

open System.Collections.Generic
open System.Threading.Tasks

type ContainerState =
    | Running
    | Stopped
    | Unknown
    | Disabled
    | Error of string
type RemoteContainer = {
    Name: string
    State: ContainerState
}
type IDockerApi = {
    getContainers: string list -> Task<Result<Map<string, ContainerState>, string>>
    getContainerState: string -> Task<Result<ContainerState, string>>
    startContainer: string -> Task<Result<ContainerState, string>>
}
[<CLIMutable>]
type Container = {
    DisplayName: string
    DisplayImage: string
    Name: string
    Enabled: bool
    State: ContainerState
} with
    static member Unknown =
        { DisplayName = "Unknown"; DisplayImage = ""; Name = "Unknown"; Enabled = false; State = Unknown  }

type ContainerConfig() =
    member val Containers = List<Container>()

