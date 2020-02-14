module Types

open Microsoft.FSharp.Reflection
open System.Collections.Generic
open System.Threading.Tasks

let private unionToString (x:'a) = 
    match FSharpValue.GetUnionFields(x, typeof<'a>) with
    | case, _ -> case.Name

type ContainerState =
    | Running
    | Stopped
    | Unknown
    | Disabled
    | Error of string
    override this.ToString() = unionToString this
        
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

