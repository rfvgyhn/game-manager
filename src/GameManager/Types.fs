module Types

open Microsoft.FSharp.Reflection

let private unionToString (x:'a) = 
    match FSharpValue.GetUnionFields(x, typeof<'a>) with
    | case, _ -> case.Name

type ServerState =
    | Running
    | Starting
    | Stopped
    | Stopping
    | Unknown
    | Disabled
    | Fetching
    | Error of string
    override this.ToString() = unionToString this
        
type AzureVmConfig = { SubscriptionId: string; ResourceGroup: string; VmName: string }
type DockerConfig = { Name: string }
[<RequireQualifiedAccess>]
type ServerType =
    | AzureVm of AzureVmConfig
    | Docker of DockerConfig
    with member self.Id =
          match self with
          | ServerType.AzureVm c -> $"{c.ResourceGroup}_{c.VmName}"
          | ServerType.Docker c -> c.Name

type Server = {
    DisplayName: string
    DisplayImage: string
    Enabled: bool
    State: ServerState
    Notes: string
    Type: ServerType
} with
    member self.Id = self.Type.Id

[<RequireQualifiedAccess>]
type ServerConfig = {
    DisplayName: string
    DisplayImage: string option
    Enabled: bool
    Notes: string option
    Type: ServerType
} with
    member self.AsServer() = {
        DisplayName = self.DisplayName
        DisplayImage = self.DisplayImage |> Option.defaultValue ""
        Enabled = self.Enabled
        State = ServerState.Unknown
        Notes = self.Notes |> Option.defaultValue ""
        Type = self.Type
    }
type AppConfig = { Servers: Server list }

