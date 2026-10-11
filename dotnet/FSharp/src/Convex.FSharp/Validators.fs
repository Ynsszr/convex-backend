namespace Convex.FSharp

open System
open System.Text


/// Convex Validator AST matching the official ValidatorJSON schema
[<RequireQualifiedAccess>]
type RecordKeyValidator =
    | String
    | Id of tableName: string
    | Union of RecordKeyValidator list

type ObjectField =
    {
        FieldType: Validator
        Optional: bool
    }

and [<RequireQualifiedAccess>] Validator =
    | Null
    | Number
    | BigInt
    | CommitTs
    | Boolean
    | String
    | Bytes
    | Any
    | LiteralString of string
    | LiteralInt64 of int64
    | LiteralFloat of float
    | LiteralBoolean of bool
    | Id of tableName: string
    | Array of element: Validator
    | Record of keys: RecordKeyValidator * values: Validator
    | Object of fields: Map<string, ObjectField>
    | Union of members: Validator list

module ValidatorJson =
    let quote (text: string) =
        let sb = StringBuilder()
        sb.Append('"') |> ignore
        for c in text do
            match c with
            | '"' -> sb.Append("\\\"") |> ignore
            | '\\' -> sb.Append("\\\\") |> ignore
            | c when int c < 32 -> sb.Append(sprintf "\\u%04x" (int c)) |> ignore
            | c -> sb.Append(c) |> ignore
        sb.Append('"') |> ignore
        sb.ToString()

    let rec private serializeRecordKey (key: RecordKeyValidator) : string =
        match key with
        | RecordKeyValidator.String -> "{\"type\":\"string\"}"
        | RecordKeyValidator.Id t -> sprintf "{\"type\":\"id\",\"tableName\":%s}" (quote t)
        | RecordKeyValidator.Union members ->
            let inner = members |> List.map serializeRecordKey |> String.concat ","
            sprintf "{\"type\":\"union\",\"value\":[%s]}" inner

    let rec toJson (v: Validator) : string =
        match v with
        | Validator.Null -> "{\"type\":\"null\"}"
        | Validator.Number -> "{\"type\":\"number\"}"
        | Validator.BigInt -> "{\"type\":\"bigint\"}"
        | Validator.CommitTs -> "{\"type\":\"commitTs\"}"
        | Validator.Boolean -> "{\"type\":\"boolean\"}"
        | Validator.String -> "{\"type\":\"string\"}"
        | Validator.Bytes -> "{\"type\":\"bytes\"}"
        | Validator.Any -> "{\"type\":\"any\"}"
        | Validator.LiteralString s ->
            sprintf "{\"type\":\"literal\",\"value\":%s}" (quote s)
        | Validator.LiteralInt64 i ->
            let b64 = ConvexBase64.encodeInt64 i
            sprintf "{\"type\":\"literal\",\"value\":{\"$integer\":\"%s\"}}" b64
        | Validator.LiteralFloat f ->
            if ConvexBase64.isSpecialFloat f then
                let b64 = ConvexBase64.encodeFloat64 f
                sprintf "{\"type\":\"literal\",\"value\":{\"$float\":\"%s\"}}" b64
            else
                sprintf "{\"type\":\"literal\",\"value\":%s}" (f.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
        | Validator.LiteralBoolean b ->
            let bStr = if b then "true" else "false"
            sprintf "{\"type\":\"literal\",\"value\":%s}" bStr
        | Validator.Id t ->
            sprintf "{\"type\":\"id\",\"tableName\":%s}" (quote t)
        | Validator.Array elem ->
            sprintf "{\"type\":\"array\",\"value\":%s}" (toJson elem)
        | Validator.Record (keys, values) ->
            sprintf "{\"type\":\"record\",\"keys\":%s,\"values\":{\"fieldType\":%s,\"optional\":false}}"
                (serializeRecordKey keys)
                (toJson values)
        | Validator.Object fields ->
            let sb = StringBuilder()
            sb.Append("{\"type\":\"object\",\"value\":{") |> ignore
            let entries = fields |> Map.toList
            entries
            |> List.iteri (fun i (k, f) ->
                if i > 0 then sb.Append(",") |> ignore
                let optStr = if f.Optional then "true" else "false"
                sb.Append(sprintf "%s:{\"fieldType\":%s,\"optional\":%s}" (quote k) (toJson f.FieldType) optStr) |> ignore
            )
            sb.Append("}}") |> ignore
            sb.ToString()
        | Validator.Union members ->
            let inner = members |> List.map toJson |> String.concat ","
            sprintf "{\"type\":\"union\",\"value\":[%s]}" inner

type Validator with
    member this.ToJson() : string = ValidatorJson.toJson this

/// Builder functions for constructing Convex validators fluently
module V =
    let private uniqueFields fields =
        let mutable seen=Set.empty
        for name in fields do
            if isNull name || seen.Contains name then invalidArg "fields" "Validator field names must be non-null and unique"
            seen <- seen.Add name

    let null' () = Validator.Null
    let ``null`` () = Validator.Null
    let number () = Validator.Number
    let float64 () = Validator.Number
    let int64 () = Validator.BigInt
    let commitTs () = Validator.CommitTs
    let boolean () = Validator.Boolean
    let string () = Validator.String
    let bytes () = Validator.Bytes
    let any () = Validator.Any
    let literalString s = Validator.LiteralString s
    let literalInt64 i = Validator.LiteralInt64 i
    let literalFloat f = Validator.LiteralFloat f
    let literalBoolean b = Validator.LiteralBoolean b
    let id tableName = Validator.Id tableName
    let array elem = Validator.Array elem
    let record keys values = Validator.Record (keys, values)

    let object (fields: (string * Validator) list) =
        uniqueFields (fields |> List.map fst)
        let map =
            fields
            |> List.map (fun (name, v) -> name, { FieldType = v; Optional = false })
            |> Map.ofList
        Validator.Object map

    let objectWithOptional (fields: (string * Validator * bool) list) =
        uniqueFields (fields |> List.map (fun (name,_,_) -> name))
        let map =
            fields
            |> List.map (fun (name, v, opt) -> name, { FieldType = v; Optional = opt })
            |> Map.ofList
        Validator.Object map

    let union (members: Validator list) =
        match members with
        | [ single ] -> single
        | _ -> Validator.Union members

    let optional (v: Validator) : (Validator * bool) =
        (v, true)

    let required (v: Validator) : (Validator * bool) =
        (v, false)

    let nullable (v: Validator) : Validator =
        union [ v; null' () ]

    /// Object transformations preserve optionality; omitted keys must exist.
    let private fieldsOf = function
        | Validator.Object fields -> fields
        | _ -> invalidArg "validator" "An object validator is required"
    let pick names validator =
        let fields=fieldsOf validator
        uniqueFields names
        names |> List.map (fun name ->
            match Map.tryFind name fields with
            | Some value -> name,value
            | None -> invalidArg "names" ("Unknown validator field: " + name))
        |> Map.ofList |> Validator.Object
    let omit names validator =
        let fields=fieldsOf validator
        pick names validator |> ignore
        Validator.Object (List.fold (fun result name -> Map.remove name result) fields names)
    let partial validator =
        fieldsOf validator |> Map.map (fun _ field -> {field with Optional=true}) |> Validator.Object
    let extend additions validator =
        uniqueFields (additions |> List.map fst)
        List.fold (fun fields (name,field) -> Map.add name field fields) (fieldsOf validator) additions |> Validator.Object

module Validators =
    /// Canonical native validator metadata, consumed by registration and schema owners.
    let toNativeValidator (validator:Validator) = validator
    /// Retained API name for native consumers of the previous projection surface.
    let toJsValidator (validator:Validator) : obj = toNativeValidator validator |> box
