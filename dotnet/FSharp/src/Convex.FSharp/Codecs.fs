namespace Convex.FSharp

open System
open System.Text

module ConvexJson =
    let private quote (s: string) =
        let sb = StringBuilder()
        sb.Append('"') |> ignore
        for c in s do
            match c with
            | '"' -> sb.Append("\\\"") |> ignore
            | '\\' -> sb.Append("\\\\") |> ignore
            | c when int c < 32 -> sb.Append(sprintf "\\u%04x" (int c)) |> ignore
            | c -> sb.Append(c) |> ignore
        sb.Append('"') |> ignore
        sb.ToString()

    /// Serializes a ConvexValue to Convex-compliant JSON wire format
    let rec toJson (value: ConvexValue) : string =
        match value with
        | ConvexValue.Null -> "null"
        | ConvexValue.Boolean b -> if b then "true" else "false"
        | ConvexValue.Int64 i ->
            let b64 = ConvexBase64.encodeInt64 i
            sprintf "{\"$integer\":\"%s\"}" b64
        | ConvexValue.Float64 f ->
            if ConvexBase64.isSpecialFloat f then
                let b64 = ConvexBase64.encodeFloat64 f
                sprintf "{\"$float\":\"%s\"}" b64
            else
                f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        | ConvexValue.String s ->
            quote s
        | ConvexValue.Bytes b ->
            let b64 = ConvexBase64.encodeBytes b
            sprintf "{\"$bytes\":\"%s\"}" b64
        | ConvexValue.Array items ->
            let inner = items |> List.map toJson |> String.concat ","
            sprintf "[%s]" inner
        | ConvexValue.Object map ->
            let sb = StringBuilder()
            sb.Append("{") |> ignore
            let entries = map |> Map.toList
            entries
            |> List.iteri (fun i (k, v) ->
                if i > 0 then sb.Append(",") |> ignore
                sb.Append(sprintf "%s:%s" (quote k) (toJson v)) |> ignore
            )
            sb.Append("}") |> ignore
            sb.ToString()
        | ConvexValue.CommitTs ->
            "{\"$commitTs\":null}"

/// Canonical native object/presence operations. Absence is a stable sentinel,
/// distinct from null, and field access never invokes CLR property getters.
module NativeValue =
    let private missingValue = obj()
    let undefinedValue () : obj = missingValue

    let createNullPrototypeObject () : obj =
        System.Collections.Generic.Dictionary<string, obj>() :> obj

    let createSimpleObject () : obj =
        System.Collections.Generic.Dictionary<string, obj>() :> obj

    let setProperty (target: obj) (key: string) (value: obj) : unit =
        match target with
        | :? System.Collections.Generic.Dictionary<string, obj> as d -> d.[key] <- value
        | _ -> ()

    let getProperty (target: obj) (key: string) : obj =
        match target with
        | :? System.Collections.Generic.Dictionary<string, obj> as d ->
            match d.TryGetValue(key) with
            | true, v -> v
            | false, _ -> undefinedValue()
        | _ -> undefinedValue()

    let hasProperty (target: obj) (key: string) : bool =
        match target with
        | :? System.Collections.Generic.Dictionary<string, obj> as d -> d.ContainsKey(key)
        | _ -> false

    let objectKeys (target: obj) : string[] =
        match target with
        | :? System.Collections.Generic.Dictionary<string, obj> as d -> Seq.toArray d.Keys
        | _ -> [||]

    let isArray (value: obj) : bool =
        match value with
        | :? System.Array -> true
        | _ -> false

    let isPlainObject (value: obj) : bool = value :? System.Collections.Generic.Dictionary<string, obj>

    let jsTypeOf (value: obj) : string =
        if obj.ReferenceEquals(value, null) then "object"
        elif value :? string then "string"
        elif value :? bool then "boolean"
        elif value :? int64 then "bigint"
        elif value :? int || value :? float || value :? float32 || value :? int16 || value :? uint16 || value :? uint32 || value :? byte || value :? sbyte then "number"
        else "object"

    let isNull (value: obj) : bool = obj.ReferenceEquals(value, null)
    let isUndefined (value: obj) : bool = obj.ReferenceEquals(value, missingValue)

/// Compatibility names for existing native consumers. All behavior belongs
/// to NativeValue; this module contains no JavaScript bindings or runtime.
module JsHelpers =
    let undefinedValue = NativeValue.undefinedValue
    let createNullPrototypeObject = NativeValue.createNullPrototypeObject
    let createSimpleObject = NativeValue.createSimpleObject
    let setProperty = NativeValue.setProperty
    let getProperty = NativeValue.getProperty
    let hasProperty = NativeValue.hasProperty
    let objectKeys = NativeValue.objectKeys
    let isArray = NativeValue.isArray
    let isPlainObject = NativeValue.isPlainObject
    let jsTypeOf = NativeValue.jsTypeOf
    let isNull = NativeValue.isNull
    let isUndefined = NativeValue.isUndefined

/// Codec for converting F# types into canonical native Convex object values
module ObjectCodec =
    open NativeValue

    /// Creates a plain JavaScript object suitable for Convex isSimpleObject
    let emptyPlainObject () : obj =
        createSimpleObject ()

    /// Sets a field on a plain JS object, omitting undefined/None
    let setOptionalField (target: obj) (key: string) (value: 'T option) (encode: 'T -> obj) =
        match value with
        | Some v -> setProperty target key (encode v)
        | None -> ()

    /// Sets a NullOr field on a plain JS object
    let setNullOrField (target: obj) (key: string) (value: NullOr<'T>) (encode: 'T -> obj) =
        match value with
        | NullOr.Null -> setProperty target key null
        | NullOr.Value v -> setProperty target key (encode v)

    /// Sets an OptionalNull field on a plain JS object
    let setOptionalNullField (target: obj) (key: string) (value: OptionalNull<'T>) (encode: 'T -> obj) =
        match value with
        | OptionalNull.Missing -> ()
        | OptionalNull.Null -> setProperty target key null
        | OptionalNull.Value v -> setProperty target key (encode v)

    /// Encodes a discriminated union into standard Convex { kind: "Tag", ... } plain object
    let encodeUnionCase (caseName: string) (fields: (string * obj) list) : obj =
        let target = createSimpleObject ()
        setProperty target "kind" (box caseName)
        for (k, v) in fields do
            setProperty target k v
        target


/// Snapshot/validate an untyped native Convex value, rather than guessing a CLR/F# codec.
module RawValueCodec =
    open NativeValue

    let validateFieldName (name: string) =
        // Match convex/values object-value fields; document root restrictions
        // belong to database adapters, not to arbitrary nested values.
        if name.Length > 1024 || name.StartsWith("$") ||
           (name |> Seq.exists (fun c -> int c < 32 || int c >= 127)) then
            failwithf "Invalid Convex object field '%s': expected non-control ASCII, at most %d characters, without a leading '$'." name 1024

    let internal utf8Length (text: string) =
        Encoding.UTF8.GetByteCount(text)

    /// Bounds mirror upstream value containers; operation/document limits remain owner checks.
    let private normalizeCore copyBytes (value: obj) : obj =
        let mutable size = 0
        let account path amount =
            if amount > 33554432 - size then failwithf "Convex value exceeds 32 MiB at %s." path
            size <- size + amount
        let rec walk (ancestors: obj list) depth (path: string) (value: obj) : obj =
            if isUndefined value then failwithf "Undefined is not a Convex value at %s. Object fields may be omitted; array positions may not." path
            elif isNull value then account path 1; null
            elif jsTypeOf value = "string" then
                account path (2 + utf8Length (unbox<string> value))
                value
            elif jsTypeOf value = "boolean" then account path 1; value
            elif jsTypeOf value = "number" then account path 9; value
            elif jsTypeOf value = "bigint" then
                account path 9
                value
            elif value :? CommitTimestamp then
                match unbox<CommitTimestamp> value with
                | CommitTimestamp.Placeholder -> account path 9; value
                | CommitTimestamp.Value timestamp -> account path 9; box timestamp
            elif value :? byte[] then
                let bytes = unbox<byte[]> value
                account path (2 + bytes.Length)
                if copyBytes then box (Array.copy bytes) else value
            else
                if ancestors |> List.exists (fun parent -> obj.ReferenceEquals(parent, value)) then
                    failwithf "Cyclic object/array is not a Convex value at %s." path
                let next = value :: ancestors
                if isArray value || isPlainObject value then
                    if depth >= 64 then failwithf "Convex value exceeds 64 container levels at %s." path
                    account path 2
                if isArray value then
                    let array = unbox<System.Array> value
                    if array.Rank <> 1 || array.GetLowerBound(0) <> 0 then failwithf "Expected a zero-based one-dimensional Convex array at %s." path
                    if array.Length > 8192 then failwithf "Convex array exceeds 8192 entries at %s." path
                    let target:obj array = if copyBytes then Array.zeroCreate array.Length else [||]
                    for i in 0..array.Length-1 do
                        let normalized=walk next (depth+1) (sprintf "%s[%d]" path i) (array.GetValue(i))
                        if copyBytes then target.[i]<-normalized
                    if copyBytes then box target else value
                elif isPlainObject value then
                    // Object.entries reads every own enumerable getter once before recursion,
                    // matching the SDK even when a nested getter changes a later source field.
                    let entries = objectKeys value |> Array.map (fun key -> key,getProperty value key)
                    let fields = entries |> Array.filter (fun (_, child) -> not (isUndefined child))
                    if fields.Length > 1024 then failwithf "Convex object exceeds 1024 retained fields at %s." path
                    let target = if copyBytes then ObjectCodec.emptyPlainObject() else null
                    for key,child in fields |> Array.sortWith (fun (a,_) (b,_) -> String.CompareOrdinal(a,b)) do
                        validateFieldName key
                        account path (key.Length + 1)
                        let normalized=walk next (depth+1) (path + "." + key) child
                        if copyBytes then setProperty target key normalized
                    if copyBytes then target else value
                else failwithf "Unsupported native Convex value at %s. Use plain objects, dense arrays, primitives or ArrayBuffer bytes; encode F# domain values with their declared codec first." path
        walk [] 0 "$" value


    /// Snapshot externally supplied native values, including mutable byte buffers.
    let normalize value = normalizeCore true value

    /// Check newly encoded domain output without duplicating its byte buffers.
    /// Domain byte encoding keeps its existing ownership behavior; callers must
    /// not mutate a buffer during an asynchronous platform operation.
    let validate value = normalizeCore false value |> ignore
