namespace Convex.FSharp

open System
open Convex.Runtime

/// Phantom markers preserve the operation and visibility selected by its owner.
type QueryKind = private QueryKind of unit
type IScheduledFunctionKind = interface end
[<Sealed>]
type MutationKind private () = interface IScheduledFunctionKind
[<Sealed>]
type ActionKind private () = interface IScheduledFunctionKind
type PublicVisibility = private PublicVisibility of unit
type InternalVisibility = private InternalVisibility of unit

[<RequireQualifiedAccess>]
type FunctionTarget =
    | Named of string
    | Component of componentName: string * functionPath: string
    | Handle of string

/// A reference keeps the selected operation, visibility, arguments and result
/// together. Durable addresses are independent of CLR and source-file names.
/// Frontend metadata is explicitly erased only for native catalogue tooling.
type ICallableContract =
    abstract Arguments: IBoundaryContract
    abstract Returns: IBoundaryContract

[<Sealed>]
type FunctionReference<'Kind, 'Visibility, 'Args, 'Result> internal
    (name: string, target: FunctionTarget, kind: FunctionKind, visibility: FunctionVisibility,
     arguments: BoundaryContract<'Args>, returns: BoundaryContract<'Result>) =
    member _.Name = name
    member _.Target = target
    member _.Kind = kind
    member _.Visibility = visibility
    member _.Arguments = arguments
    member _.Returns = returns
    interface ICallableContract with
        member _.Arguments=arguments :> IBoundaryContract
        member _.Returns=returns :> IBoundaryContract
    member _.Address =
        match target with
        | FunctionTarget.Named name -> name
        | FunctionTarget.Handle value -> value
        | FunctionTarget.Component(componentName, path) ->
            "_reference/childComponent/" + componentName + "/" + path.Replace(":", "/")
    member internal this.ToNativeTarget() : obj = box this.Address

type PublicQueryReference<'Args, 'Result> = FunctionReference<QueryKind, PublicVisibility, 'Args, 'Result>
type PublicMutationReference<'Args, 'Result> = FunctionReference<MutationKind, PublicVisibility, 'Args, 'Result>
type PublicActionReference<'Args, 'Result> = FunctionReference<ActionKind, PublicVisibility, 'Args, 'Result>
type InternalQueryReference<'Args, 'Result> = FunctionReference<QueryKind, InternalVisibility, 'Args, 'Result>
type InternalMutationReference<'Args, 'Result> = FunctionReference<MutationKind, InternalVisibility, 'Args, 'Result>
type InternalActionReference<'Args, 'Result> = FunctionReference<ActionKind, InternalVisibility, 'Args, 'Result>

[<Sealed>]
type FunctionHandle<'Kind, 'Visibility, 'Args, 'Result> internal
    (value: string, reference: FunctionReference<'Kind, 'Visibility, 'Args, 'Result>) =
    member _.Value = value
    member _.Reference = reference
    override _.ToString() = value

module FunctionReference =
    let private validText label maximum (value: string) =
        if String.IsNullOrWhiteSpace value || value.Length>maximum || (value |> Seq.exists Char.IsControl) then
            invalidArg label "A bounded canonical function address is required"
    let internal named name =
        validText "name" 1024 name
        if not(Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_/-]+:[A-Za-z0-9_]+$")) then
            invalidArg "name" "A durable module:function address is required"
        FunctionTarget.Named name
    let internal create<'Kind, 'Visibility, 'Args, 'Result> kind visibility name target (arguments: BoundaryContract<'Args>) (returns: BoundaryContract<'Result>) =
        if not(ValidatorCheck.isArgumentEnvelope arguments.Validator) then
            invalidArg "arguments" "A function reference requires an object argument contract"
        FunctionReference<'Kind, 'Visibility, 'Args, 'Result>(name, target, kind, visibility, arguments, returns)

    let publicQuery name arguments returns : PublicQueryReference<_, _> =
        create FunctionKind.Query FunctionVisibility.Public name (named name) arguments returns
    let publicMutation name arguments returns : PublicMutationReference<_, _> =
        create FunctionKind.Mutation FunctionVisibility.Public name (named name) arguments returns
    let publicAction name arguments returns : PublicActionReference<_, _> =
        create FunctionKind.Action FunctionVisibility.Public name (named name) arguments returns
    let internalQuery name arguments returns : InternalQueryReference<_, _> =
        create FunctionKind.Query FunctionVisibility.Internal name (named name) arguments returns
    let internalMutation name arguments returns : InternalMutationReference<_, _> =
        create FunctionKind.Mutation FunctionVisibility.Internal name (named name) arguments returns
    let internalAction name arguments returns : InternalActionReference<_, _> =
        create FunctionKind.Action FunctionVisibility.Internal name (named name) arguments returns

    let inComponent componentName (reference: FunctionReference<'Kind, 'Visibility, 'Args, 'Result>) =
        validText "component" 1024 componentName
        if componentName.Split('/') |> Array.exists(fun value ->
            String.IsNullOrEmpty value || not(Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$"))) then
            invalidArg "component" "A canonical child component path is required"
        match reference.Target with
        | FunctionTarget.Named path ->
            create<'Kind, 'Visibility, 'Args, 'Result> reference.Kind reference.Visibility
                (componentName + ":" + path) (FunctionTarget.Component(componentName, path)) reference.Arguments reference.Returns
        | _ -> invalidArg "reference" "Mount a named reference in its owning component once"

    /// The export name can differ from the owning function's durable address.
    /// Native code generation resolves the actual component graph before
    /// preserving this exact direct-child export path.
    let atComponentExport componentName attributes (reference: FunctionReference<'Kind, 'Visibility, 'Args, 'Result>) =
        validText "component" 1024 componentName
        if componentName.Contains('/') || componentName.Contains('\\') then
            invalidArg "component" "A component export belongs to one direct child"
        if List.isEmpty attributes then invalidArg "attributes" "A callable component export requires a leaf path"
        for attribute in attributes do
            validText "attribute" 256 attribute
            if attribute.Contains('/') || attribute.Contains('\\') || attribute.Contains(':') then
                invalidArg "attributes" "A component export attribute is one path segment"
        create<'Kind, 'Visibility, 'Args, 'Result> reference.Kind reference.Visibility reference.Name
            (FunctionTarget.Component(componentName,String.concat "/" attributes)) reference.Arguments reference.Returns

    let withHandle value (reference: FunctionReference<'Kind, 'Visibility, 'Args, 'Result>) =
        validText "handle" 2048 value
        if not(value.StartsWith("function://", StringComparison.Ordinal)) then
            invalidArg "handle" "An owning Convex function handle is required"
        let target =
            create<'Kind, 'Visibility, 'Args, 'Result> reference.Kind reference.Visibility
                reference.Name (FunctionTarget.Handle value) reference.Arguments reference.Returns
        FunctionHandle<'Kind, 'Visibility, 'Args, 'Result>(value, target)

    let ofHandle (handle: FunctionHandle<'Kind, 'Visibility, 'Args, 'Result>) = handle.Reference

/// Explicit external contracts are useful when consuming an upstream component;
/// generated native catalogues bind these types to its actual exported metadata.
type ApiModule(moduleName: string) =
    member _.Query<'Args, 'Result>(name, arguments: BoundaryContract<'Args>, returns: BoundaryContract<'Result>) =
        FunctionReference.publicQuery (moduleName + ":" + name) arguments returns
    member _.Mutation<'Args, 'Result>(name, arguments: BoundaryContract<'Args>, returns: BoundaryContract<'Result>) =
        FunctionReference.publicMutation (moduleName + ":" + name) arguments returns
    member _.Action<'Args, 'Result>(name, arguments: BoundaryContract<'Args>, returns: BoundaryContract<'Result>) =
        FunctionReference.publicAction (moduleName + ":" + name) arguments returns

module Api =
    let module' name = ApiModule name
