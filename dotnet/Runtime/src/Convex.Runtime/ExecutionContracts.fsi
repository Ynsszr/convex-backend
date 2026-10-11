namespace Convex.Runtime

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks

type FunctionKind = Query = 0 | Mutation = 1 | Action = 2 | HttpAction = 3
type FunctionVisibility = Public = 0 | Internal = 1
type DeclarationKind = Schema = 0 | Authentication = 1 | CronJobs = 2 | HttpRouter = 3 | Component = 4

module ContractNames =
    val functionKind: FunctionKind -> string
    val visibility: FunctionVisibility -> string
    val parseKind: string -> FunctionKind
    val parseVisibility: string -> FunctionVisibility
    val declarationKind: DeclarationKind -> string

/// One invocation's capabilities carry canonical wire values. No language
/// frontend's argument, database, or error representation appears in this ABI.
type IInvocationHost =
    abstract InvocationId: string
    abstract CallAsync: name:string * arguments:JsonElement * asynchronous:bool -> Task<JsonElement>
    abstract Call: name:string * arguments:JsonElement -> JsonElement

type IExecutionFrontend =
    abstract Enter: host:IInvocationHost -> IDisposable

type IRegisteredFunction =
    abstract Kind: FunctionKind
    abstract Visibility: FunctionVisibility
    abstract Arguments: JsonElement
    abstract Returns: JsonElement
    abstract ExecutionHandler: Delegate
    abstract Invoke: host:IInvocationHost * arguments:JsonElement * httpRequest:JsonElement -> Task<JsonElement>

type IHttpActionRegistration =
    abstract Function: IRegisteredFunction

type IDeclaration =
    abstract Kind: DeclarationKind
    abstract Export: unit -> JsonElement

type IComponentDefinition =
    inherit IDeclaration
    abstract IsApplication: bool
    abstract Initializers: IReadOnlyList<Delegate>
    abstract Initialize: child:string * arguments:JsonElement -> JsonElement

type HttpRoute =
    new: methodName:string * path:string * prefix:bool * handler:IRegisteredFunction -> HttpRoute
    member Method: string
    member Path: string
    member Prefix: bool
    member Handler: IRegisteredFunction

type IHttpRouterDefinition =
    inherit IDeclaration
    abstract Routes: IReadOnlyList<HttpRoute>

type IApplicationError =
    abstract EncodedData: JsonElement

type ApplicationException =
    inherit Exception
    new: message:string * data:JsonElement -> ApplicationException
    member EncodedData: JsonElement
    interface IApplicationError

type HostOperationException =
    inherit InvalidOperationException
    new: code:string * message:string -> HostOperationException
    member Code: string

module RuntimeJson =
    val parse: text:string -> JsonElement
    val objectValue: fields:(string*obj) list -> obj
    val element: fields:(string*obj) list -> JsonElement
    val nullValue: JsonElement
