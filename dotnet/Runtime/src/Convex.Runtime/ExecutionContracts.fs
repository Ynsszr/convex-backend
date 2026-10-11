namespace Convex.Runtime

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks

/// CLR contracts shared by language frontends and the Convex execution worker.
/// JSON here is the canonical Convex wire representation, including tagged
/// integers, bytes and floats. A language frontend owns its domain conversion.
type FunctionKind =
    | Query = 0
    | Mutation = 1
    | Action = 2
    | HttpAction = 3

type FunctionVisibility =
    | Public = 0
    | Internal = 1

type DeclarationKind =
    | Schema = 0
    | Authentication = 1
    | CronJobs = 2
    | HttpRouter = 3
    | Component = 4

module ContractNames =
    let functionKind = function
        | FunctionKind.Query -> "query"
        | FunctionKind.Mutation -> "mutation"
        | FunctionKind.Action -> "action"
        | FunctionKind.HttpAction -> "httpAction"
        | _ -> invalidArg "kind" "Unknown native function kind"

    let visibility = function
        | FunctionVisibility.Public -> "public"
        | FunctionVisibility.Internal -> "internal"
        | _ -> invalidArg "visibility" "Unknown native function visibility"

    let parseKind = function
        | "query" -> FunctionKind.Query
        | "mutation" -> FunctionKind.Mutation
        | "action" -> FunctionKind.Action
        | "httpAction" -> FunctionKind.HttpAction
        | _ -> invalidArg "kind" "Unknown native function kind"

    let parseVisibility = function
        | "public" -> FunctionVisibility.Public
        | "internal" -> FunctionVisibility.Internal
        | _ -> invalidArg "visibility" "Unknown native function visibility"

    let declarationKind = function
        | DeclarationKind.Schema -> "schema"
        | DeclarationKind.Authentication -> "auth"
        | DeclarationKind.CronJobs -> "crons"
        | DeclarationKind.HttpRouter -> "http"
        | DeclarationKind.Component -> "component"
        | _ -> invalidArg "kind" "Unknown native declaration kind"

/// This capability belongs to one invocation. It contains no deployment key,
/// transaction commit operation, or worker-supplied read-set authority.
type IInvocationHost =
    abstract InvocationId: string
    abstract CallAsync: name: string * arguments: JsonElement * asynchronous: bool -> Task<JsonElement>
    abstract Call: name: string * arguments: JsonElement -> JsonElement

/// A frontend enters its own scoped codec/identity environment around both
/// declaration discovery and invocation; the execution engine knows no F# types.
type IExecutionFrontend =
    abstract Enter: host: IInvocationHost -> IDisposable

type IRegisteredFunction =
    abstract Kind: FunctionKind
    abstract Visibility: FunctionVisibility
    abstract Arguments: JsonElement
    abstract Returns: JsonElement
    abstract ExecutionHandler: Delegate
    abstract Invoke: host: IInvocationHost * arguments: JsonElement * httpRequest: JsonElement -> Task<JsonElement>

/// HTTP routing accepts an HTTP registration capability, not an arbitrary
/// query/mutation registration or an untyped exported value.
type IHttpActionRegistration =
    abstract Function: IRegisteredFunction

type IDeclaration =
    abstract Kind: DeclarationKind
    abstract Export: unit -> JsonElement

type IComponentDefinition =
    inherit IDeclaration
    abstract IsApplication: bool
    abstract Initializers: IReadOnlyList<Delegate>
    abstract Initialize: child: string * arguments: JsonElement -> JsonElement

type HttpRoute(methodName: string, path: string, prefix: bool, handler: IRegisteredFunction) =
    member _.Method = methodName
    member _.Path = path
    member _.Prefix = prefix
    member _.Handler = handler

type IHttpRouterDefinition =
    inherit IDeclaration
    abstract Routes: IReadOnlyList<HttpRoute>

type IApplicationError =
    abstract EncodedData: JsonElement

/// Safe application-error data is forwarded unchanged. Runtime faults remain
/// distinct from a function's structured, deliberately returned application error.
type ApplicationException(message: string, data: JsonElement) =
    inherit Exception(message)
    let encodedData=data.Clone()
    member _.EncodedData = encodedData
    interface IApplicationError with member _.EncodedData = encodedData

type HostOperationException(code: string, message: string) =
    inherit InvalidOperationException(message)
    member _.Code = code

module RuntimeJson =
    let parse (text: string) =
        use document = JsonDocument.Parse text
        document.RootElement.Clone()

    let objectValue (fields: (string * obj) list) : obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for name, item in fields do value.Add(name, item)
        box value

    let element fields =
        objectValue fields |> fun value -> JsonSerializer.SerializeToElement(value, value.GetType())

    let nullValue = parse "null"
