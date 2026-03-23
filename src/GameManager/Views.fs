module Views

open GameManager.ViewEngine.HtmlElements.MuJs
open Giraffe.ViewEngine
open System
open Types

let private layout (content: XmlNode list) =
    html [] [
        head [] [
            meta [_charset "utf-8"]
            meta [_name "viewport"; _content "width=device-width, initial-scale=1" ]
            title [] [encodedText "Game Manager"]
            link [_rel "stylesheet"; _href "https://cdnjs.cloudflare.com/ajax/libs/bulma/0.8.0/css/bulma.min.css" ]
            link [ _rel  "stylesheet"; _type "text/css"; _href "/main.css" ]
        ]
        body [_class "container"] [
            section [_class "section"] [
                yield! content                
            ]
            rawText
                """
                <svg style="display: none;">
                  <symbol id="icon-play" viewBox="0 0 448 512">
                    <path d="M424.4 214.7L72.4 6.6C43.8-10.3 0 6.1 0 47.9V464c0 37.5 40.7 60.1 72.4 41.3l352-208c31.4-18.5 31.5-64.1 0-82.6z"/>
                  </symbol>
                </svg>
                """
            script [
                _src "https://unpkg.com/@digicreon/mujs@1.4.4/dist/mu.min.js"
                _integrity "sha384-HOmrsf1xbQSkv1hsu7+gOO5LVzWpEJif8c/3sbuupYBt9DTk+PD5cn9khN324tvv"
                _crossorigin "anonymous"
            ] []
            script [ _src "/main.js" ] []
        ]
    ]

let private spinner() =
    span [ _class "button is-small rounded is-info is-loading" ] [ ]

let private startButton name state =
    let action = $"/servers/%s{name}/start"
    let classes =
        [
            "button"; "is-small"; "rounded"
            if state = Starting then "is-info is-loading" else "is-success"
        ] |> String.concat " "
    form [ _class ""; _action action; _method "POST"; mu (Mode Patch) ] [
        button [ _type "submit"; _class classes; _title "Start Server" ] [
            rawText @"<svg class=""icon-play""><use href=""#icon-play""></use></svg>"
        ]
    ]
    
let tag id state =
    let cssClass, title, text =
        match state with
        | Stopping
        | Stopped -> ("is-warning", "", None)
        | Running -> ("is-success", "", None)
        | Creating
        | Created
        | Starting -> ("is-info", "", None)
        | Disabled -> ("", "", None)
        | Initializing s -> ("is-info", "", None)
        | Fetching -> ("is-info is-loading", "Fetching status", None)
        | ServerState.Unknown -> ("is-info", "", None)
        | Error m -> ("is-danger", m, None)

    span [_class "status"; mu (PatchTarget $"#%s{id} .status") ] [
        span [_class $"tag %s{cssClass}"; _title title] [
            text |> Option.defaultWith(fun () -> ServerState.asString state) |> encodedText
        ]
        if state.IsStopped || state.IsStarting then startButton id state
        if state.IsInitializing then spinner()
    ]
    

let card server =
    let image =
        if String.IsNullOrEmpty(server.DisplayImage) then
            "placeholder.png"
        else
            $"cards/%s{server.DisplayImage}"
    
    li [_id server.Id; _class $"card %A{server.State}"; mu (PatchTarget $"#%s{server.Id}")] [
        div [_class "card-image" ] [
            figure [_class "image is-4by3"] [
                img [_src image; ]
            ]
        ]
        div [_class "card-content"] [
            tag server.Id server.State
            div [_class "media"] [
                div [_class "media-content"] [
                    p [_class "title is-4"] [ encodedText server.DisplayName]
                ]
            ]
            div [ _class "content" ] [
                encodedText server.Notes
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
            div [ _id "sse"; mu (Trigger Load); mu (Url "/sse"); mu (Mode Patch); mu (Method Sse) ] []
            span [ _id "sse-disconnected"; _class "tag is-warning" ] [
                encodedText "Disconnected. You won't receive status updates."
            ]
    ]
