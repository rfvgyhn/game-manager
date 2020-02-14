# Docker Game Manager [![Docker Pulls]][0]

Tool to allow your friends to start stopped containers. Useful for when your host charges based on CPU usage.
Allows you to turn off your game servers when no players are on but not have to be around when your buddies
want to play.

# Server Config File

Managed containers need to be specified in a config file.

    docker run -d \
               --name game_manager \
               -v $(pwd)/appsettings.json:/app/appsettings.json \
               -v /etc/localtime:/etc/localtime:ro \
               rfvgyhn/game-manager
    
Sample `appsettings.json`:

    {
        "Containers": [
            {
                "DisplayName": "Container1 - Name",
                "DisplayImage": "container1.png",
                "Name": "container_name",
                "Enabled": true
            },
            {
                "DisplayName": "Container2 - Name",
                "DisplayImage": "container2.png",
                "Name": "container_name2",
                "Enabled": false
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


Name         | Type    | Description 
-------------|---------|--------------
DisplayName  | string  | The text that shows up in the card title
DisplayImage | string  | Path to card image relative to the `cards` directory
Name         | string  | Container name as shown by `docker ps`
Enabled      | boolean | Allow users to interact with container

# Card Images

Each container has support for a custom image. Mount the cards volume in order to add them.

    docker run -d \
               --name game_manager \
               -v $(pwd)/appsettings.json:/app/appsettings.json \
               -v $(pwd)/cards:/app/wwwroot/cards \
               -v /etc/localtime:/etc/localtime:ro \
               rfvgyhn/game-manager
               
# Authorization

This app doesn't support authentication/authorization out of the box. It is highly recommended to run this behind
 a reverse proxy that takes care of auth for you. [Traefik] with [Let's Encrypt] + [Basic Auth] makes this very simple.

[Docker Pulls]: https://img.shields.io/docker/pulls/rfvgyhn/game-manager.svg
[Traefik]: https://docs.traefik.io/
[Let's Encrypt]: https://docs.traefik.io/https/acme/
[Basic Auth]: https://docs.traefik.io/middlewares/basicauth/
[0]: https://hub.docker.com/r/rfvgyhn/game-manager