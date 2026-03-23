module Types

open System
open System.Text.Json.Serialization
open Microsoft.FSharp.Reflection

let private unionToString (x:'a) = 
    match FSharpValue.GetUnionFields(x, typeof<'a>) with
    | case, _ -> case.Name

type ServerState =
    | Creating
    | Created
    | Running
    | Starting
    | Initializing of {| Description: string option; Progress: float option |}
    | Stopped
    | Stopping
    | Unknown
    | Disabled
    | Fetching
    | Error of string
    override this.ToString() = unionToString this
module ServerState =
    let private (|Eq|_|) (actual: string) (target: string) =
        if actual.Equals(target, StringComparison.OrdinalIgnoreCase) then Some ()
        else None
    let tryParse (s: string) (description: string option) (progress: float option) =
        match s with
        | Eq "created" -> Some Created
        | Eq "creating" -> Some Creating
        | Eq "disabled" -> Some Disabled
        | Eq "fetching" -> Some Fetching
        | Eq "initializing" -> Some (Initializing {| Description = description; Progress = progress |})
        | Eq "running" -> Some Running
        | Eq "starting" -> Some Starting
        | Eq "stopped" -> Some Stopped
        | Eq "stopping" -> Some Stopping
        | Eq "unknown" -> Some Unknown
        | _ -> None
        
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
type ServerStatusMode = Pull | Push
type Server = {
    DisplayName: string
    DisplayImage: string
    Enabled: bool
    State: ServerState
    Notes: string
    Type: ServerType
    StatusMode: ServerStatusMode
} with
    member self.Id = self.Type.Id

[<RequireQualifiedAccess>]
type ServerConfig = {
    DisplayName: string
    DisplayImage: string option
    Enabled: bool
    Notes: string option
    Type: ServerType
    [<JsonPropertyName("StatusMode")>]
    RawStatusMode: ServerStatusMode option
} with
    member self.StatusMode = defaultArg self.RawStatusMode Pull

    member self.AsServer() = {
        DisplayName = self.DisplayName
        DisplayImage = self.DisplayImage |> Option.defaultValue ""
        Enabled = self.Enabled
        State = if self.Enabled then ServerState.Unknown else ServerState.Disabled
        Notes = self.Notes |> Option.defaultValue ""
        Type = self.Type
        StatusMode = self.StatusMode
    }
type AppConfig = {
    AzureEventGridSharedSecret: string option
    Servers: Server list
    SseHeartbeatInterval: TimeSpan
    StatusPollingInterval: TimeSpan
}

