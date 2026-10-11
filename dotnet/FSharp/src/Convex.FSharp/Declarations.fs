namespace Convex.FSharp

open System
open Microsoft.FSharp.Reflection

/// Native client source is a projection of the canonical CLR contract and
/// validator. It does not define a second document or wire schema.
module Declarations =
    let quote (value:string) = ValidatorJson.quote value
    let identifier (value:string) =
        if String.IsNullOrEmpty value || value.Contains("``") || (value |> Seq.exists Char.IsControl) then
            invalidArg "name" "A bounded F# declaration identifier is required"
        "``"+value+"``"
    let qualifiedName (value:string) =
        "global."+String.concat "." (value.Replace('+','.').Split('.') |> Array.map identifier)
    let private withoutArity (value:string) =
        value.Split('.') |> Array.map(fun segment ->
            let marker=segment.IndexOf('`')
            if marker<0 then segment else segment.Substring(0,marker)) |> String.concat "."

    /// Anonymous records are structural. Named application types must belong to
    /// the supplied handler-free contract assemblies; backend DTO leakage fails.
    let typeSource (contractAssemblies:Set<string>) (selected:Type) =
        let rec render depth (value:Type) =
            if depth>64 || value.IsByRef || value.IsPointer || value.IsGenericParameter then
                invalidArg "type" "A closed bounded native client type is required"
            if value=typeof<unit> then "unit"
            elif value=typeof<string> then "string"
            elif value=typeof<obj> then "obj"
            elif value=typeof<bool> then "bool"
            elif value=typeof<float> then "float"
            elif value=typeof<float32> then "float32"
            elif value=typeof<int> then "int"
            elif value=typeof<int64> then "int64"
            elif value=typeof<byte> then "byte"
            elif value.IsArray then
                if value.GetArrayRank()<>1 then invalidArg "type" "Native client arrays must have one dimension"
                "("+render (depth+1) (value.GetElementType())+") array"
            elif FSharpType.IsTuple value then
                let body=FSharpType.GetTupleElements value |> Array.map(render (depth+1)) |> String.concat " * "
                (if value.IsValueType then "struct (" else "(")+body+")"
            elif FSharpType.IsRecord(value,System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic) && value.Name.Contains("AnonymousType",StringComparison.Ordinal) then
                let fields=FSharpType.GetRecordFields(value,System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic) |> Array.map(fun field ->
                    identifier field.Name+": "+render (depth+1) field.PropertyType) |> String.concat "; "
                (if value.IsValueType then "struct " else "")+"{| "+fields+" |}"
            else
                let assembly=value.Assembly.GetName().Name
                if not(contractAssemblies.Contains assembly) && assembly<>"FSharp.Core" &&
                   assembly<>"Convex.FSharp" && assembly<>"Convex.Runtime" &&
                   assembly<>"System.Private.CoreLib" && not(assembly.StartsWith("System.",StringComparison.Ordinal)) then
                    invalidArg "type" ("Move this callable type to its handler-free contract owner: "+value.FullName)
                if isNull value.FullName || not(value.IsPublic || value.IsNestedPublic) then
                    invalidArg "type" "Native client contract types must be public"
                if value.IsGenericType then
                    let definition=value.GetGenericTypeDefinition()
                    let arguments=value.GetGenericArguments() |> Array.map(render (depth+1))
                    let special =
                        if definition=typedefof<option<_>> then Some "option"
                        elif definition=typedefof<voption<_>> then Some "voption"
                        elif definition=typedefof<list<_>> then Some "list"
                        elif definition=typedefof<Map<_,_>> then Some "Map"
                        elif definition=typedefof<Set<_>> then Some "Set"
                        else None
                    let name=special |> Option.defaultWith(fun () -> qualifiedName(withoutArity(definition.FullName.Replace('+','.'))))
                    name+"<"+String.concat ", " arguments+">"
                else qualifiedName value.FullName
        render 0 selected

    let validatorSource validator =
        let list values="["+String.concat "; " values+"]"
        let rec key depth value =
            if depth>64 then invalidArg "validator" "Validator metadata exceeds its depth bound"
            match value with
            | RecordKeyValidator.String -> "RecordKeyValidator.String"
            | RecordKeyValidator.Id name -> "RecordKeyValidator.Id "+quote name
            | RecordKeyValidator.Union values -> "RecordKeyValidator.Union "+list(List.map(key (depth+1) >> fun value -> "("+value+")") values)
        let rec render depth value =
            if depth>64 then invalidArg "validator" "Validator metadata exceeds its depth bound"
            let child value="("+render (depth+1) value+")"
            match value with
            | Validator.Null -> "V.null'()"
            | Validator.Number -> "V.float64()"
            | Validator.BigInt -> "V.int64()"
            | Validator.CommitTs -> "V.commitTs()"
            | Validator.Boolean -> "V.boolean()"
            | Validator.String -> "V.string()"
            | Validator.Bytes -> "V.bytes()"
            | Validator.Any -> "V.any()"
            | Validator.LiteralString value -> "V.literalString "+quote value
            | Validator.LiteralInt64 value -> "V.literalInt64 ("+string value+"L)"
            | Validator.LiteralFloat value ->
                "V.literalFloat (System.BitConverter.Int64BitsToDouble("+string(BitConverter.DoubleToInt64Bits value)+"L))"
            | Validator.LiteralBoolean value -> "V.literalBoolean "+(if value then "true" else "false")
            | Validator.Id name -> "V.id "+quote name
            | Validator.Array value -> "V.array "+child value
            | Validator.Record(keys,values) -> "V.record ("+key (depth+1) keys+") "+child values
            | Validator.Union values -> "V.union "+list(List.map child values)
            | Validator.Object fields ->
                "V.objectWithOptional "+list(fields |> Map.toList |> List.map(fun (name,field) ->
                    "("+quote name+", "+child field.FieldType+", "+(if field.Optional then "true" else "false")+")"))
        render 0 validator

    /// A shared exported custom codec wins by object identity. Canonical SDK
    /// codecs retain their exact validator; nullable/array composition retains
    /// the child codec, including an authored semantic mapping.
    let contractSource contractAssemblies (owners:(IBoundaryContract*string) list) (selected:IBoundaryContract) =
        let rec render depth (value:IBoundaryContract) =
            if depth>64 then invalidArg "contract" "Contract composition exceeds its depth bound"
            match owners |> List.tryFind(fun (contract,_) -> Object.ReferenceEquals(contract,value)) with
            | Some(_,expression) -> expression
            | None ->
                let selectedType=typeSource contractAssemblies value.ValueType
                let validator=validatorSource value.Validator
                let child()=value.Element |> Option.map(render (depth+1)) |> Option.defaultWith(fun () -> invalidOp "Missing canonical child contract")
                match value.Encoding with
                | ContractEncoding.CanonicalValue -> "Contract.valueWithValidator<"+selectedType+"> ("+validator+")"
                | ContractEncoding.CanonicalArguments -> "Contract.argumentsWithValidator<"+selectedType+"> ("+validator+")"
                | ContractEncoding.Refined -> "Contract.refine ("+validator+") ("+child()+")"
                | ContractEncoding.Nullable -> "Contract.refine ("+validator+") (Contract.nullable ("+child()+"))"
                | ContractEncoding.Array -> "Contract.refine ("+validator+") (Contract.array ("+child()+"))"
                | ContractEncoding.Authored ->
                    invalidOp ("Export this custom codec from a handler-free contract module: "+value.ValueType.FullName)
        render 0 selected
