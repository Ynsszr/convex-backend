namespace Convex.FSharp

open System

/// Lossless Convex JSON wire format. This is distinct from ordinary HTTP JSON:
/// int64, bytes, special floating values, and commit placeholders use SDK tags.
module ConvexWire =
    let private maximumTextBytes=48*1024*1024
    let private checkText (text:string) =
        if isNull text || text.Length>maximumTextBytes || RawValueCodec.utf8Length(text)>maximumTextBytes then
            invalidArg "text" "Convex JSON exceeds its bounded wire envelope"

    let parseValue (text:string) : ConvexValue =
        checkText text
        use document=Text.Json.JsonDocument.Parse(text,Text.Json.JsonDocumentOptions(MaxDepth=128))
        let rec read (value:Text.Json.JsonElement) =
            match value.ValueKind with
            | Text.Json.JsonValueKind.Null -> ConvexValue.Null
            | Text.Json.JsonValueKind.True -> ConvexValue.Boolean true
            | Text.Json.JsonValueKind.False -> ConvexValue.Boolean false
            | Text.Json.JsonValueKind.String -> ConvexValue.String(value.GetString())
            | Text.Json.JsonValueKind.Number ->
                let number=value.GetDouble()
                if Double.IsNaN number || Double.IsInfinity number then invalidArg "text" "Special floats require a Convex float envelope"
                ConvexValue.Float64 number
            | Text.Json.JsonValueKind.Array -> ConvexValue.Array [for item in value.EnumerateArray() -> read item]
            | Text.Json.JsonValueKind.Object ->
                // JSON.parse uses the last occurrence of a duplicate JSON key.
                let properties=value.EnumerateObject() |> Seq.map(fun item -> item.Name,item.Value) |> Map.ofSeq
                let encoded tag decode =
                    let item=properties[tag]
                    if item.ValueKind<>Text.Json.JsonValueKind.String then invalidArg "text" "Convex encoded values require a base64 string"
                    decode(item.GetString())
                if properties.Count=1 && properties.ContainsKey "$integer" then
                    encoded "$integer" (ConvexBase64.decodeInt64 >> ConvexValue.Int64)
                elif properties.Count=1 && properties.ContainsKey "$float" then
                    encoded "$float" (fun data ->
                        let number=ConvexBase64.decodeFloat64 data
                        if not(ConvexBase64.isSpecialFloat number) then invalidArg "text" "An ordinary number cannot use a special float envelope"
                        ConvexValue.Float64 number)
                elif properties.Count=1 && properties.ContainsKey "$bytes" then
                    encoded "$bytes" (ConvexBase64.decodeBytes >> ConvexValue.Bytes)
                elif properties.Count=1 && properties.ContainsKey "$commitTs" then
                    if properties["$commitTs"].ValueKind<>Text.Json.JsonValueKind.Null then invalidArg "text" "Commit placeholder envelope must contain null"
                    ConvexValue.CommitTs
                else
                    properties |> Map.map(fun key item -> RawValueCodec.validateFieldName key;read item) |> ConvexValue.Object
            | _ -> invalidArg "text" "Invalid JSON value"
        let result=read document.RootElement
        // Apply the same native Convex value/container limits in both runtimes.
        Codec.encode<ConvexValue> result |> ignore
        result

    let inline encode<'T> (value:'T) =
        Codec.encode<'T> value |> Codec.decode<ConvexValue> |> ConvexJson.toJson
    let inline decode<'T> text : 'T = parseValue text |> Codec.encode<ConvexValue> |> Codec.decode<'T>
    let toNative text = parseValue text |> Codec.encode<ConvexValue>

/// Human-facing JSON rendering uses the same F# field and union encoding on .NET
/// and Fable. Bytes are base64; unsafe int64 values are decimal strings. Use
/// ConvexWire when exact round-trip typing, special floats or timestamps matter.
module HttpJson =
    let rec render (value:ConvexValue) =
        match value with
        | ConvexValue.Null -> "null"
        | ConvexValue.Boolean flag -> if flag then "true" else "false"
        | ConvexValue.String text -> ValidatorJson.quote text
        | ConvexValue.Int64 number ->
            if number >= -9007199254740991L && number <= 9007199254740991L then string number
            else ValidatorJson.quote(string number)
        | ConvexValue.Float64 number ->
            if Double.IsNaN number || Double.IsInfinity number then invalidArg "value" "Ordinary JSON cannot represent a nonfinite number; use Http.convexJson"
            if number=0. then "0" else
            number.ToString("R",Globalization.CultureInfo.InvariantCulture)
        | ConvexValue.Bytes bytes -> ValidatorJson.quote(ConvexBase64.encodeBytes bytes)
        | ConvexValue.Array values -> "["+(values |> List.map render |> String.concat ",")+"]"
        | ConvexValue.Object fields ->
            "{"+(fields |> Map.toList |> List.map(fun(key,item) -> ValidatorJson.quote key+":"+render item) |> String.concat ",")+"}"
        | ConvexValue.CommitTs -> invalidArg "value" "An unresolved commit timestamp is not ordinary JSON"
    let inline encode<'T> (value:'T) = Codec.encode<'T> value |> Codec.decode<ConvexValue> |> render

/// Ordinary JSON used by retained intent fingerprints. Preserve the existing
/// JavaScript field order and numeric/string spelling on the native target.
/// This is deliberately separate from the lossless Convex value wire format.
module PortableJson =
    let private quote (value:string) =
        let result=Text.StringBuilder("\"")
        let mutable index=0
        while index<value.Length do
            let character=value[index]
            match character with
            | '"' -> result.Append("\\\"") |> ignore
            | '\\' -> result.Append("\\\\") |> ignore
            | '\b' -> result.Append("\\b") |> ignore
            | '\u000c' -> result.Append("\\f") |> ignore
            | '\n' -> result.Append("\\n") |> ignore
            | '\r' -> result.Append("\\r") |> ignore
            | '\t' -> result.Append("\\t") |> ignore
            | c when int c<32 -> result.Append(sprintf "\\u%04x" (int c)) |> ignore
            | c when Char.IsHighSurrogate c && index+1<value.Length && Char.IsLowSurrogate value[index+1] ->
                result.Append(c).Append(value[index+1]) |> ignore
                index<-index+1
            | c when Char.IsSurrogate c -> result.Append(sprintf "\\u%04x" (int c)) |> ignore
            | c -> result.Append(c) |> ignore
            index<-index+1
        result.Append('"').ToString()

    let private number (value:float) =
        if Double.IsNaN value || Double.IsInfinity value then "null"
        elif value=0. then "0"
        else
            let sign=if value<0. then "-" else ""
            let text=(abs value).ToString("R",Globalization.CultureInfo.InvariantCulture)
            let parts=text.Split('E','e')
            let exponent=if parts.Length=2 then Int32.Parse(parts[1],Globalization.CultureInfo.InvariantCulture) else 0
            let coefficient=parts[0]
            let point=coefficient.IndexOf('.')
            let digits=coefficient.Replace(".","").TrimStart('0')
            let leading=coefficient.Replace(".","").Length-digits.Length
            let position=(if point<0 then coefficient.Length else point)-leading+exponent
            if position>0 && position<=21 then
                if position>=digits.Length then sign+digits+String('0',position-digits.Length)
                else sign+digits[..position-1]+"."+digits[position..]
            elif position<=0 && position> -6 then sign+"0."+String('0',-position)+digits
            else
                let rest=if digits.Length=1 then "" else "."+digits[1..]
                let exp=position-1
                sign+string digits[0]+rest+"e"+(if exp>=0 then "+" else "")+string exp

    let rec stringify (value:obj) =
        if isNull value then "null" else
        match value with
        | :? string as text -> quote text
        | :? bool as flag -> if flag then "true" else "false"
        | :? float as value -> number value
        | :? int as value -> string value
        | :? int64 -> invalidArg "value" "Ordinary JSON cannot encode a Convex int64; use ConvexWire"
        | :? (obj[]) as values ->
            "["+(values |> Array.map(fun item -> if NativeValue.isUndefined item then "null" else stringify item) |> String.concat ",")+"]"
        | :? Collections.IDictionary as fields ->
            let names=NativeValue.objectKeys value
            let numeric,ordinary=names |> Array.partition(fun name ->
                let mutable index=0u
                UInt32.TryParse(name,&index) && index<UInt32.MaxValue && string index=name)
            let ordered=Array.append (numeric |> Array.sortBy UInt32.Parse) ordinary
            "{"+(ordered |> Array.filter(fun name -> not(NativeValue.isUndefined fields[name]))
                         |> Array.map(fun name -> quote name+":"+stringify fields[name]) |> String.concat ",")+"}"
        | _ -> invalidArg "value" "Encode domain values through the canonical codec before ordinary JSON rendering"
