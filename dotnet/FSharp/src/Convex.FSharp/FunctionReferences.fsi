namespace Convex.FSharp

open Convex.Runtime

type QueryKind
type IScheduledFunctionKind = interface end
[<Sealed>]
type MutationKind =
    interface IScheduledFunctionKind
[<Sealed>]
type ActionKind =
    interface IScheduledFunctionKind
type PublicVisibility
type InternalVisibility

[<RequireQualifiedAccess>]
type FunctionTarget =
    | Named of string
    | Component of componentName:string * functionPath:string
    | Handle of string

type ICallableContract =
    abstract Arguments: IBoundaryContract
    abstract Returns: IBoundaryContract

/// Constructors stay inside the frontend. Callers select an operation through
/// its factory or a canonical registration; arbitrary kind casts are not an API.
[<Sealed>]
type FunctionReference<'Kind,'Visibility,'Args,'Result> =
    interface ICallableContract
    member Name: string
    member Target: FunctionTarget
    member Kind: FunctionKind
    member Visibility: FunctionVisibility
    member Arguments: BoundaryContract<'Args>
    member Returns: BoundaryContract<'Result>
    member Address: string
    member internal ToNativeTarget: unit -> obj

type PublicQueryReference<'Args,'Result> = FunctionReference<QueryKind,PublicVisibility,'Args,'Result>
type PublicMutationReference<'Args,'Result> = FunctionReference<MutationKind,PublicVisibility,'Args,'Result>
type PublicActionReference<'Args,'Result> = FunctionReference<ActionKind,PublicVisibility,'Args,'Result>
type InternalQueryReference<'Args,'Result> = FunctionReference<QueryKind,InternalVisibility,'Args,'Result>
type InternalMutationReference<'Args,'Result> = FunctionReference<MutationKind,InternalVisibility,'Args,'Result>
type InternalActionReference<'Args,'Result> = FunctionReference<ActionKind,InternalVisibility,'Args,'Result>

[<Sealed>]
type FunctionHandle<'Kind,'Visibility,'Args,'Result> =
    member Value: string
    member Reference: FunctionReference<'Kind,'Visibility,'Args,'Result>
    override ToString: unit -> string

module FunctionReference =
    val internal named: name:string -> FunctionTarget
    val internal create<'Kind,'Visibility,'Args,'Result>:
        kind:FunctionKind -> visibility:FunctionVisibility -> name:string -> target:FunctionTarget ->
        arguments:BoundaryContract<'Args> -> returns:BoundaryContract<'Result> ->
        FunctionReference<'Kind,'Visibility,'Args,'Result>
    val publicQuery: name:string -> arguments:BoundaryContract<'Args> -> returns:BoundaryContract<'Result> -> PublicQueryReference<'Args,'Result>
    val publicMutation: name:string -> arguments:BoundaryContract<'Args> -> returns:BoundaryContract<'Result> -> PublicMutationReference<'Args,'Result>
    val publicAction: name:string -> arguments:BoundaryContract<'Args> -> returns:BoundaryContract<'Result> -> PublicActionReference<'Args,'Result>
    val internalQuery: name:string -> arguments:BoundaryContract<'Args> -> returns:BoundaryContract<'Result> -> InternalQueryReference<'Args,'Result>
    val internalMutation: name:string -> arguments:BoundaryContract<'Args> -> returns:BoundaryContract<'Result> -> InternalMutationReference<'Args,'Result>
    val internalAction: name:string -> arguments:BoundaryContract<'Args> -> returns:BoundaryContract<'Result> -> InternalActionReference<'Args,'Result>
    val inComponent: componentName:string -> reference:FunctionReference<'Kind,'Visibility,'Args,'Result> -> FunctionReference<'Kind,'Visibility,'Args,'Result>
    val atComponentExport: componentName:string -> attributes:string list -> reference:FunctionReference<'Kind,'Visibility,'Args,'Result> -> FunctionReference<'Kind,'Visibility,'Args,'Result>
    val withHandle: value:string -> reference:FunctionReference<'Kind,'Visibility,'Args,'Result> -> FunctionHandle<'Kind,'Visibility,'Args,'Result>
    val ofHandle: handle:FunctionHandle<'Kind,'Visibility,'Args,'Result> -> FunctionReference<'Kind,'Visibility,'Args,'Result>

type ApiModule =
    new: moduleName:string -> ApiModule
    member Query<'Args,'Result>: name:string * arguments:BoundaryContract<'Args> * returns:BoundaryContract<'Result> -> PublicQueryReference<'Args,'Result>
    member Mutation<'Args,'Result>: name:string * arguments:BoundaryContract<'Args> * returns:BoundaryContract<'Result> -> PublicMutationReference<'Args,'Result>
    member Action<'Args,'Result>: name:string * arguments:BoundaryContract<'Args> * returns:BoundaryContract<'Result> -> PublicActionReference<'Args,'Result>

module Api =
    val module': name:string -> ApiModule
