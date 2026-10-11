namespace Convex.FSharp

type FunctionMetadata =
    {
        Name: string
        ComponentPath: string
        Type: string
        Visibility: string
    }

type DeploymentMetadata =
    {
        Name: string
        Region: string option
        Class: string
    }

type RequestMetadata =
    {
        Ip: string option
        UserAgent: string option
        RequestId: string
        ScheduledFunctionId: string option
        AuthToken: string option
    }

type TransactionMetric =
    {
        Used: int
        Remaining: int
    }

type TransactionMetrics =
    {
        BytesRead: TransactionMetric
        BytesWritten: TransactionMetric
        DatabaseQueries: TransactionMetric
        DocumentsRead: TransactionMetric
        DocumentsWritten: TransactionMetric
        FunctionsScheduled: TransactionMetric
        ScheduledFunctionArgsBytes: TransactionMetric
    }

type IQueryMeta =
    abstract GetFunctionMetadata: unit -> Async<FunctionMetadata>
    abstract GetDeploymentMetadata: unit -> Async<DeploymentMetadata>
    abstract GetTransactionMetrics: unit -> Async<TransactionMetrics>
    abstract GetSnapshotTs: unit -> int64

type IMutationMeta =
    inherit IQueryMeta
    abstract GetRequestMetadata: unit -> Async<RequestMetadata>

type IActionMeta =
    abstract GetFunctionMetadata: unit -> Async<FunctionMetadata>
    abstract GetDeploymentMetadata: unit -> Async<DeploymentMetadata>
    abstract GetRequestMetadata: unit -> Async<RequestMetadata>

/// Custom limits for a nested transaction call. Values are capped at global transaction limits.
type TransactionLimits =
    {
        BytesRead: int option
        BytesWritten: int option
        DatabaseQueries: int option
        DocumentsRead: int option
        DocumentsWritten: int option
        FunctionsScheduled: int option
        ScheduledFunctionArgsBytes: int option
    }
    static member Empty =
        {
            BytesRead = None
            BytesWritten = None
            DatabaseQueries = None
            DocumentsRead = None
            DocumentsWritten = None
            FunctionsScheduled = None
            ScheduledFunctionArgsBytes = None
        }

/// Options for nested query execution.
type RunQueryOptions =
    {
        TransactionLimits: TransactionLimits option
    }
    static member Default = { TransactionLimits = None }

/// Options for nested query execution from within a mutation,
/// optionally enabling reading from a recent snapshot.
type MutationRunQueryOptions =
    {
        TransactionLimits: TransactionLimits option
        UseStaleSnapshot: bool option
    }
    static member Default = { TransactionLimits = None; UseStaleSnapshot = None }

/// Options for nested mutation execution.
type RunMutationOptions =
    {
        TransactionLimits: TransactionLimits option
    }
    static member Default = { TransactionLimits = None }

type QueryCtx =
    abstract Db: IDatabaseReader
    abstract Auth: IAuth
    abstract Storage: IStorageReader
    abstract Meta: IQueryMeta
    abstract RunQueryRaw: target: obj * args: obj * options: obj -> Async<obj>

type MutationCtx =
    abstract Db: IDatabaseWriter
    abstract Auth: IAuth
    abstract Scheduler: IScheduler
    abstract Storage: IStorageWriter
    abstract Meta: IMutationMeta
    abstract RunQueryRaw: target: obj * args: obj * options: obj -> Async<obj>
    abstract RunMutationRaw: target: obj * args: obj * options: obj -> Async<obj>

type VectorSearchResult<'Table> =
    {
        Id: Id<'Table>
        Score: float
    }

/// Convex vector indexes admit equality predicates and OR expressions only.
type VectorFilterBuilder(raw:obj) =
    member _.Raw=raw
    member inline this.Eq(field:string,value:'T):obj =
        NativeExpressions.binary "$eq" (NativeExpressions.field field) (Codec.encode<'T> value)
    member this.EqMissing(field:string):obj =
        NativeExpressions.binary "$eq" (NativeExpressions.field field) (NativeValue.undefinedValue())
    member this.Or(expressions:obj seq):obj =
        NativeExpressions.logic "$or" expressions

module VectorSearchValidation =
    let validate (vector:float[]) limit =
        if isNull vector || vector.Length=0 || vector.Length>4096 || vector |> Array.exists (fun value -> System.Double.IsNaN value || System.Double.IsInfinity value) then
            invalidArg "vector" "A finite non-empty vector with at most 4096 dimensions is required"
        if limit |> Option.exists (fun value -> value<1 || value>256) then invalidArg "limit" "Vector search limit must be between 1 and 256"

/// Validate the captured canonical vector filter AST without executing its
/// callback again. Literal contents are values, never expression dispatch.
module CheckedVectorFilter =
    let validate allowed expression =
        let mutable visited=0
        let rec walk depth value =
            visited<-visited+1
            if depth>16 || visited>256 || not(NativeValue.isPlainObject value) then
                invalidArg "filter" "A bounded vector equality/OR expression is required"
            match NativeValue.objectKeys value with
            | [|"$or"|] ->
                match NativeValue.getProperty value "$or" with
                | :? (obj[]) as values when values.Length>0 && values.Length<=256 ->
                    for item in values do walk (depth+1) item
                | _ -> invalidArg "filter" "Vector OR requires bounded nonempty expressions"
            | [|"$eq"|] ->
                match NativeValue.getProperty value "$eq" with
                | :? (obj[]) as operands when operands.Length=2 ->
                    let field,literal=operands[0],operands[1]
                    if NativeValue.objectKeys field<>[|"$field"|] || NativeValue.objectKeys literal<>[|"$literal"|] then
                        invalidArg "filter" "Vector equality requires one declared field and a literal"
                    match NativeValue.getProperty field "$field" with
                    | :? string as name when List.contains name allowed -> ()
                    | _ -> invalidArg "filter" "Vector filter field is not declared in this index"
                | _ -> invalidArg "filter" "Vector equality requires two operands"
            | _ -> invalidArg "filter" "Only vector equality and OR expressions are supported"
        walk 0 expression

/// Convex-managed services admitted by the pinned SDK release. The owning
/// backend resolves availability and issues the invocation-scoped credential.
[<RequireQualifiedAccess>]
type ServiceName = AiGateway

module ServiceName =
    let internal value = function ServiceName.AiGateway -> "ai-gateway"

type ActionCtx =
    abstract Auth: IAuth
    abstract Scheduler: IScheduler
    abstract Storage: IStorageActionWriter
    abstract Meta: IActionMeta
    abstract RunQueryRaw: target: obj * args: obj -> Async<obj>
    abstract RunMutationRaw: target: obj * args: obj -> Async<obj>
    abstract RunActionRaw: target: obj * args: obj -> Async<obj>
    abstract VectorSearchRaw: tableName: string * indexName: string * vector: float[] * limit: int option -> Async<obj[]>
    abstract VectorSearchFilteredRaw: tableName:string * indexName:string * vector:float[] * limit:int option * filter:(obj -> obj) option -> Async<obj[]>
    abstract GetServiceTokenRaw: service: string -> Async<string>
    abstract GetServiceUrlRaw: service: string -> Async<string>

module NestedOptionsHelper =
    let inline serializeLimits (limits: TransactionLimits) : obj =
        for value in [limits.BytesRead;limits.BytesWritten;limits.DatabaseQueries;limits.DocumentsRead;limits.DocumentsWritten;limits.FunctionsScheduled;limits.ScheduledFunctionArgsBytes] do
            if value |> Option.exists (fun limit -> limit<0) then invalidArg "limits" "Nested transaction limits cannot be negative"
        let target = NativeValue.createSimpleObject ()
        limits.BytesRead |> Option.iter (fun v -> NativeValue.setProperty target "bytesRead" (box v))
        limits.BytesWritten |> Option.iter (fun v -> NativeValue.setProperty target "bytesWritten" (box v))
        limits.DatabaseQueries |> Option.iter (fun v -> NativeValue.setProperty target "databaseQueries" (box v))
        limits.DocumentsRead |> Option.iter (fun v -> NativeValue.setProperty target "documentsRead" (box v))
        limits.DocumentsWritten |> Option.iter (fun v -> NativeValue.setProperty target "documentsWritten" (box v))
        limits.FunctionsScheduled |> Option.iter (fun v -> NativeValue.setProperty target "functionsScheduled" (box v))
        limits.ScheduledFunctionArgsBytes |> Option.iter (fun v -> NativeValue.setProperty target "scheduledFunctionArgsBytes" (box v))
        target

    let inline serializeRunQueryOptions (opts: RunQueryOptions) : obj =
        let target = NativeValue.createSimpleObject ()
        opts.TransactionLimits |> Option.iter (fun lim -> NativeValue.setProperty target "transactionLimits" (serializeLimits lim))
        target

    let inline serializeMutationRunQueryOptions (opts: MutationRunQueryOptions) : obj =
        let target = NativeValue.createSimpleObject ()
        opts.TransactionLimits |> Option.iter (fun lim -> NativeValue.setProperty target "transactionLimits" (serializeLimits lim))
        opts.UseStaleSnapshot |> Option.iter (fun v -> NativeValue.setProperty target "useStaleSnapshot" (box v))
        target

    let inline serializeRunMutationOptions (opts: RunMutationOptions) : obj =
        let target = NativeValue.createSimpleObject ()
        opts.TransactionLimits |> Option.iter (fun lim -> NativeValue.setProperty target "transactionLimits" (serializeLimits lim))
        target

[<AutoOpen>]
module ContextExtensions =
    type QueryCtx with
        member this.RunQuery<'Visibility, 'Args, 'Ret>(fn: FunctionReference<QueryKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            async {
                let encodedArgs = fn.Arguments.Encode args
                let! res = this.RunQueryRaw(fn.ToNativeTarget(), encodedArgs, null)
                return fn.Returns.Decode res
            }

        member this.RunQuery<'Visibility, 'Args, 'Ret>(fn: FunctionReference<QueryKind, 'Visibility, 'Args, 'Ret>, args: 'Args, options: RunQueryOptions) : Async<'Ret> =
            async {
                let encodedArgs = fn.Arguments.Encode args
                let jsOpts = NestedOptionsHelper.serializeRunQueryOptions options
                let! res = this.RunQueryRaw(fn.ToNativeTarget(), encodedArgs, jsOpts)
                return fn.Returns.Decode res
            }

        member this.RunQuery<'Visibility, 'Args, 'Ret>(handle: FunctionHandle<QueryKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            this.RunQuery(FunctionReference.ofHandle handle, args)

        member this.RunQuery<'Visibility, 'Args, 'Ret>(handle: FunctionHandle<QueryKind, 'Visibility, 'Args, 'Ret>, args: 'Args, options: RunQueryOptions) : Async<'Ret> =
            this.RunQuery(FunctionReference.ofHandle handle, args, options)

    type MutationCtx with
        member this.RunQuery<'Visibility, 'Args, 'Ret>(fn: FunctionReference<QueryKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            async {
                let encodedArgs = fn.Arguments.Encode args
                let! res = this.RunQueryRaw(fn.ToNativeTarget(), encodedArgs, null)
                return fn.Returns.Decode res
            }

        member this.RunQuery<'Visibility, 'Args, 'Ret>(fn: FunctionReference<QueryKind, 'Visibility, 'Args, 'Ret>, args: 'Args, options: MutationRunQueryOptions) : Async<'Ret> =
            async {
                let encodedArgs = fn.Arguments.Encode args
                let jsOpts = NestedOptionsHelper.serializeMutationRunQueryOptions options
                let! res = this.RunQueryRaw(fn.ToNativeTarget(), encodedArgs, jsOpts)
                return fn.Returns.Decode res
            }

        member this.RunQuery<'Visibility, 'Args, 'Ret>(handle: FunctionHandle<QueryKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            this.RunQuery(FunctionReference.ofHandle handle, args)

        member this.RunQuery<'Visibility, 'Args, 'Ret>(handle: FunctionHandle<QueryKind, 'Visibility, 'Args, 'Ret>, args: 'Args, options: MutationRunQueryOptions) : Async<'Ret> =
            this.RunQuery(FunctionReference.ofHandle handle, args, options)

        member this.RunMutation<'Visibility, 'Args, 'Ret>(fn: FunctionReference<MutationKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            async {
                let encodedArgs = fn.Arguments.Encode args
                let! res = this.RunMutationRaw(fn.ToNativeTarget(), encodedArgs, null)
                return fn.Returns.Decode res
            }

        member this.RunMutation<'Visibility, 'Args, 'Ret>(fn: FunctionReference<MutationKind, 'Visibility, 'Args, 'Ret>, args: 'Args, options: RunMutationOptions) : Async<'Ret> =
            async {
                let encodedArgs = fn.Arguments.Encode args
                let jsOpts = NestedOptionsHelper.serializeRunMutationOptions options
                let! res = this.RunMutationRaw(fn.ToNativeTarget(), encodedArgs, jsOpts)
                return fn.Returns.Decode res
            }

        member this.RunMutation<'Visibility, 'Args, 'Ret>(handle: FunctionHandle<MutationKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            this.RunMutation(FunctionReference.ofHandle handle, args)

        member this.RunMutation<'Visibility, 'Args, 'Ret>(handle: FunctionHandle<MutationKind, 'Visibility, 'Args, 'Ret>, args: 'Args, options: RunMutationOptions) : Async<'Ret> =
            this.RunMutation(FunctionReference.ofHandle handle, args, options)

    type ActionCtx with
        member this.RunQuery<'Visibility, 'Args, 'Ret>(fn: FunctionReference<QueryKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            async {
                let encodedArgs = fn.Arguments.Encode args
                let! res = this.RunQueryRaw(fn.ToNativeTarget(), encodedArgs)
                return fn.Returns.Decode res
            }

        member this.RunQuery<'Visibility, 'Args, 'Ret>(handle: FunctionHandle<QueryKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            this.RunQuery(FunctionReference.ofHandle handle, args)

        member this.RunMutation<'Visibility, 'Args, 'Ret>(fn: FunctionReference<MutationKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            async {
                let encodedArgs = fn.Arguments.Encode args
                let! res = this.RunMutationRaw(fn.ToNativeTarget(), encodedArgs)
                return fn.Returns.Decode res
            }

        member this.RunMutation<'Visibility, 'Args, 'Ret>(handle: FunctionHandle<MutationKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            this.RunMutation(FunctionReference.ofHandle handle, args)

        member this.RunAction<'Visibility, 'Args, 'Ret>(fn: FunctionReference<ActionKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            async {
                let encodedArgs = fn.Arguments.Encode args
                let! res = this.RunActionRaw(fn.ToNativeTarget(), encodedArgs)
                return fn.Returns.Decode res
            }

        member this.RunAction<'Visibility, 'Args, 'Ret>(handle: FunctionHandle<ActionKind, 'Visibility, 'Args, 'Ret>, args: 'Args) : Async<'Ret> =
            this.RunAction(FunctionReference.ofHandle handle, args)

        member this.GetServiceToken(service: ServiceName) : Async<string> =
            this.GetServiceTokenRaw(ServiceName.value service)

        member this.GetServiceUrl(service: ServiceName) : Async<string> =
            this.GetServiceUrlRaw(ServiceName.value service)

        member inline this.VectorSearch<'Table>(tableName: string, indexName: string, vector: float[], ?limit: int, ?filter: VectorFilterBuilder -> obj) : Async<VectorSearchResult<'Table> list> =
            async {
                VectorSearchValidation.validate vector limit
                let! rawList =
                    match filter with
                    | None -> this.VectorSearchRaw(tableName,indexName,vector,limit)
                    | Some predicate -> this.VectorSearchFilteredRaw(tableName,indexName,vector,limit,Some(fun raw -> predicate(VectorFilterBuilder raw)))
                return [
                    for item in rawList ->
                        let identifier=NativeValue.getProperty item "_id"
                        let scoreValue=NativeValue.getProperty item "_score"
                        if NativeValue.jsTypeOf identifier<>"string" || NativeValue.jsTypeOf scoreValue<>"number" then invalidOp "Vector search returned malformed result metadata"
                        let idStr=unbox<string> identifier
                        let score=System.Convert.ToDouble scoreValue
                        if System.String.IsNullOrEmpty idStr || System.Double.IsNaN score || System.Double.IsInfinity score then invalidOp "Vector search returned an invalid document identity or score"
                        { Id = Id idStr; Score = score }
                ]
            }

        member inline this.VectorSearch<'Table, 'Doc>(table: Table<'Table, 'Doc>, indexName: string, vector: float[], ?limit: int, ?filter: VectorFilterBuilder -> obj) : Async<VectorSearchResult<'Table> list> =
            this.VectorSearch<'Table>(table.Name, indexName, vector, ?limit = limit, ?filter=filter)

        member this.VectorSearch<'Table, 'Doc>(index: DeclaredTableVectorIndex<'Table, 'Doc>, vector: float[], ?limit: int, ?filter: VectorFilterBuilder -> obj) : Async<VectorSearchResult<'Table> list> = async {
            VectorSearchValidation.validate vector limit
            if vector.Length<>index.Dimensions then invalidArg "vector" "Vector dimensions differ from the canonical index declaration"
            let captured=filter |> Option.map(fun build ->
                let expression=build(VectorFilterBuilder null) |> NativeExpressions.serialize
                CheckedVectorFilter.validate index.FilterFields expression
                NativeFilterExpression expression :> obj)
            return! this.VectorSearch<'Table>(index.TableName,index.IndexName,vector,?limit=limit,
                ?filter=(captured |> Option.map(fun expression -> fun _ -> expression)))
        }


