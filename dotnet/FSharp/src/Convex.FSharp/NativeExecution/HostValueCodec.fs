namespace Convex.FSharp.NativeExecution

open System
open System.IO
open System.Text.Json
open Convex.FSharp

module internal HostValueCodec =
    let maximumFrame = 16 * 1024 * 1024

    let rec private writeNative (writer: Utf8JsonWriter) (value: obj) =
        if NativeValue.isUndefined value then
            writer.WriteStartObject()
            writer.WriteNull("$undefined")
            writer.WriteEndObject()
        elif isNull value then writer.WriteNullValue()
        else
            match value with
            | :? JsonElement as element -> element.WriteTo writer
            | :? string as text -> writer.WriteStringValue text
            | :? bool as flag -> writer.WriteBooleanValue flag
            | :? int as number -> writer.WriteNumberValue number
            | :? float as number ->
                if ConvexBase64.isSpecialFloat number then
                    writer.WriteStartObject()
                    writer.WriteString("$float", ConvexBase64.encodeFloat64 number)
                    writer.WriteEndObject()
                else writer.WriteNumberValue number
            | :? int64 as number ->
                writer.WriteStartObject()
                writer.WriteString("$integer", ConvexBase64.encodeInt64 number)
                writer.WriteEndObject()
            | :? (byte[]) as bytes ->
                writer.WriteStartObject()
                writer.WriteString("$bytes", ConvexBase64.encodeBytes bytes)
                writer.WriteEndObject()
            | :? CommitTimestamp as stamp when stamp=CommitTimestamp.Placeholder ->
                writer.WriteStartObject()
                writer.WriteNull("$commitTs")
                writer.WriteEndObject()
            | :? Collections.IDictionary as fields ->
                writer.WriteStartObject()
                for key in fields.Keys do
                    match key with
                    | :? string as name ->
                        writer.WritePropertyName name
                        writeNative writer fields[key]
                    | _ -> invalidArg "value" "Protocol object keys must be strings"
                writer.WriteEndObject()
            | :? (obj[]) as values ->
                writer.WriteStartArray()
                for item in values do writeNative writer item
                writer.WriteEndArray()
            | _ -> invalidArg "value" "Unsupported protocol value; encode domain values through Convex.FSharp"

    let encode value =
        use memory = new MemoryStream()
        use writer = new Utf8JsonWriter(memory)
        writeNative writer value
        writer.Flush()
        if memory.Length>int64 maximumFrame then invalidArg "value" "Runtime frame exceeds 16 MiB"
        memory.ToArray()

    let element value =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(encode value))
        document.RootElement.Clone()

