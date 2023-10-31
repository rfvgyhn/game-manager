# Game Manager [![Docker Pulls]][0]

Tool to allow your friends to start stopped servers. Useful for when your host charges based on CPU usage.
Allows you to turn off your game servers when no players are on but not have to be around when your buddies
want to play.

![Screenshot]

# Server Config File

Managed servers need to be specified in a config file.

    docker run -d \
               --name game_manager \
               -v $(pwd)/appsettings.json:/app/appsettings.json \
               -v /etc/localtime:/etc/localtime:ro \
               -v /var/run/docker.sock:/var/run/docker.sock:ro \
               rfvgyhn/game-manager
    
Sample `appsettings.json`:

    {
        "Servers": [
            {
                "DisplayName": "Server1 - Name",
                "DisplayImage": "server1.png",
                "Notes": "Some notes about this container",
                "Type": {
                    "Docker": {
                        "Name": "container_name"
                    }
                }
            },
            {
                "DisplayName": "Server2 - Name",
                "DisplayImage": "server2.png",
                "Enabled": false,
                "Type": {
                    "AzureVm": {
                        "VmName": "vm-name",
                        "ResourceGroup": "rg-name",
                        "SubscriptionId": "8d8d9eb6-4031-4a60-b10a-94a18605d2a9"
                    }
                }
            }
        ],
        "Logging": {
            "LogLevel": {
                "GameManager": "Debug",
                "Default": "Error",
                "System": "Error",
                "Microsoft": "Error"
            }
        }
    }


| Name                        | Type    | Required | Description                                               | 
|-----------------------------|---------|----------|-----------------------------------------------------------|
| DisplayName                 | string  | Yes      | The text that shows up in the card title                  |
| DisplayImage                | string  | No       | Path to card image relative to the `cards` directory      |
| Enabled                     | boolean | No       | Allow users to interact with container                    |
| Notes                       | string  | No       | Optional text that shows up in the card description       |
| Type                        | object  | Yes      | Can be Docker or AzureVM                                  |
| Type.Docker.Name            | string  | Yes      | Name of docker container                                  |
| Type.AzureVm.VmName         | string  | Yes      | Name of Azure virtual machine                             |
| Type.AzureVm.ResourceGroup  | string  | Yes      | Name of the resource group the virtual machine belongs to |
| Type.AzureVm.SubscriptionId | string  | Yes      | Id of the subscription the resource group belongs to      |

### Azure Permissions
In order to start and get the status of VMs, this app needs the following permissions:
* `Microsoft.Compute/virtualMachines/start/action`
* `Microsoft.Compute/virtualMachines/instanceView/read`

[Environment] and [Managed Identity] credentials are supported.

# Card Images

Each server has support for a custom image. Mount the cards volume in order to add them.

    docker run -d \
               --name game_manager \
               -v $(pwd)/appsettings.json:/app/appsettings.json \
               -v $(pwd)/cards:/app/wwwroot/cards \
               -v /etc/localtime:/etc/localtime:ro \
               -v /var/run/docker.sock:/var/run/docker.sock:ro \
               rfvgyhn/game-manager
               
# Authorization

This app doesn't support authentication/authorization out of the box. It is highly recommended to run this behind
 a reverse proxy that takes care of auth for you. [Traefik] with [Let's Encrypt] + [Basic Auth] makes this very simple.

[Docker Pulls]: https://img.shields.io/docker/pulls/rfvgyhn/game-manager.svg
[Traefik]: https://docs.traefik.io/
[Let's Encrypt]: https://docs.traefik.io/https/acme/
[Basic Auth]: https://docs.traefik.io/middlewares/basicauth/
[Screenshot]: screenshot.png?raw=true
[0]: https://hub.docker.com/r/rfvgyhn/game-manager
[Environment]: https://learn.microsoft.com/en-us/dotnet/api/azure.identity.environmentcredential
[Managed Identity]: https://learn.microsoft.com/en-us/dotnet/api/azure.identity.managedidentitycredential