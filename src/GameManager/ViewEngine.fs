namespace GameManager.ViewEngine

open Giraffe.ViewEngine

module HtmlElements =
    module MuJs =
        type Mode = Patch
        type Trigger = Load
        type Method = Sse
        type Attr =
            | Method of Method
            | Mode of Mode
            | PatchMode of Mode
            | PatchTarget of string
            | Source of string
            | Target of string
            | Trigger of Trigger
            | Url of string
            
        let mu attribute =
            let name, value =
                match attribute with
                | Method m -> "method", m.ToString().ToLowerInvariant()
                | Mode m -> "mode", m.ToString().ToLowerInvariant()
                | PatchMode m -> "patch-mode", m.ToString().ToLowerInvariant()
                | PatchTarget t -> "patch-target", t
                | Source s -> "source", s
                | Target t -> "target", t
                | Trigger t -> "trigger", t.ToString().ToLowerInvariant()
                | Url u -> "url", u
            
            attr $"mu-%s{name}" value