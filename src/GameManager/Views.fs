module Views

open System
open Types
open Giraffe.ViewEngine

let layout (content: XmlNode list) =
    html [] [
        head [] [
            meta [_charset "utf-8"]
            meta [_name "viewport"; _content "width=device-width, initial-scale=1" ]
            title [] [encodedText "Game Manager"]
            link [_rel "stylesheet"; _href "https://maxcdn.bootstrapcdn.com/font-awesome/4.7.0/css/font-awesome.min.css" ]
            link [_rel "stylesheet"; _href "https://cdnjs.cloudflare.com/ajax/libs/bulma/0.8.0/css/bulma.min.css" ]
            link [ _rel  "stylesheet"; _type "text/css"; _href "/main.css" ]
        ]
        body [_class "container"] [
            section [_class "section"] [
                yield! content                
            ]            
            script [ _src "/main.js" ] []
        ]
    ]

let private startButton name state =
    let action = $"/servers/%s{name}/start"
    let classes =
        [
            "button"; "is-small"; "rounded"
            if state = Starting then "is-info is-loading" else "is-success"
        ] |> String.concat " "
    form [ _class ""; _action action; _method "POST" ] [
        button [ _type "submit"; _class classes; _title "Start Server" ] [
            i [ _class "fa fa-play" ] []
        ]
    ]
let private tag c =
    let (cssClass, title) =
        match c.State with
        | Stopping
        | Stopped -> ("is-warning", "")
        | Running -> ("is-success", "")
        | Starting -> ("is-info", "")
        | Disabled -> ("", "")
        | Fetching -> ("is-info is-loading", "Fetching status")
        | ServerState.Unknown -> ("is-info", "")
        | Error m -> ("is-danger", m)
    span [_class "status"] [
        span [_class $"tag %s{cssClass}"; _title title] [
            encodedText <| c.State.ToString()
        ]
        if c.State = Stopped || c.State = Starting then startButton c.Id c.State
    ]
    

let card c =
    let image =
        if String.IsNullOrEmpty(c.DisplayImage) then
            "placeholder.png"
        else
            $"cards/%s{c.DisplayImage}"
    
    li [_class $"card %A{c.State}"; _data "name" c.Id] [
        div [_class "card-image" ] [
            figure [_class "image is-4by3"] [
                img [_src image; ]
            ]
        ]
        div [_class "card-content"] [
            tag c
            div [_class "media"] [
                div [_class "media-content"] [
                    p [_class "title is-4"] [ encodedText c.DisplayName]
                ]
            ]
            div [ _class "content" ] [
                encodedText c.Notes
            ]
        ]
    ]
    
    
let private noServersFound =
    let sample ="""
"Servers": [
    {
        "DisplayName": "Server 1",
        "DisplayImage": "image/path/relative/to/cards/dir.png",
        "Type": {
            "Docker": {
                "Name": "container_name"
            }
        }
    },
    {
        "DisplayName": "Server 2",
        "Enabled": false,
        "Type": {
            "AzureVm": {
                "VmName": "vm-name",
                "ResourceGroup": "rg-name",
                "SubscriptionId": "8d8d9eb6-4031-4a60-b10a-94a18605d2a9"
            }
        }
    }
}]"""
    article [ _class "message" ] [
        div [ _class "message-header" ] [
            p [] [ encodedText "No Servers Found" ]
        ]
        div [ _class "message-body" ] [
            p [] [ encodedText "Add some servers to your configuration." ]
            pre [] [ encodedText sample ]
        ]
    ]
    
let index servers = layout <| [
    match servers with
    | [] -> noServersFound
    | _ ->
        ul [_class ""] 
            (servers |> List.map card)
]
