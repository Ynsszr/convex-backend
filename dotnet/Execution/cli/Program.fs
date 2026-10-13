module Convex.DotNet.Cli

open System
open System.Threading
open System.Text.Json
open Convex.DotNet.Deployment

let private credential variable =
    if String.IsNullOrWhiteSpace variable || variable.Length>128 ||
       (variable |> Seq.exists(fun c -> not(Char.IsAsciiLetterOrDigit c || c='_'))) then
        invalidArg "credentialVariable" "Select a deployment credential environment variable"
    Environment.GetEnvironmentVariable variable

let private usage = """Native Convex development and deployment client

  convex-dotnet new NAME [DIRECTORY]
  convex-dotnet component new NAME [DIRECTORY]
  convex-dotnet build convex.project.json
  convex-dotnet codegen convex.project.json
  convex-dotnet dev convex.project.json [--runtime PATH] [--port PORT] [--site-port PORT] [--once]
  convex-dotnet watch convex.project.json
  convex-dotnet runtime inspect DISTRIBUTION
  convex-dotnet runtime prepare DISTRIBUTION NEW_SELECTION_DIRECTORY
  convex-dotnet runtime run DISTRIBUTION DATA_DIRECTORY -- BACKEND_ARGUMENTS

build compiles and packages the application and generates typed clients.
codegen/package use already compiled inputs. dev owns a loopback backend and deploys
source edits to that local development instance. watch only rebuilds without deployment.
runtime prepare freezes a selected distribution.

  convex-dotnet run ORIGIN query|mutation|action MODULE:FUNCTION ARGUMENTS_JSON_FILE ADMIN_VARIABLE
  convex-dotnet env list ORIGIN ADMIN_VARIABLE
  convex-dotnet env set ORIGIN NAME VALUE_VARIABLE ADMIN_VARIABLE
  convex-dotnet env remove ORIGIN NAME ADMIN_VARIABLE
  convex-dotnet logs ORIGIN CURSOR ADMIN_VARIABLE
  convex-dotnet tables ORIGIN ADMIN_VARIABLE
  convex-dotnet export ORIGIN NEW_ZIP_FILE ADMIN_VARIABLE [--include-storage]
  convex-dotnet import ORIGIN FORMAT TABLE_OR_DASH FILE requireEmpty|append|replace ADMIN_VARIABLE

  convex-dotnet review ABSOLUTE_CAPSULE_DIRECTORY
  convex-dotnet review-project ABSOLUTE_NATIVE_PROJECT_JSON
  convex-dotnet deploy ORIGIN ABSOLUTE_CAPSULE_DIRECTORY ADMIN_KEY_VARIABLE
  convex-dotnet deploy-project ORIGIN ABSOLUTE_NATIVE_PROJECT_JSON ADMIN_KEY_VARIABLE
  convex-dotnet deploy-graph ORIGIN ABSOLUTE_COMPILED_GRAPH_JSON ADMIN_KEY_VARIABLE
  convex-dotnet reconcile ORIGIN ADMIN_KEY_VARIABLE [--deployment-identity NAME]

review and review-project inspect already packaged native inputs without deploying.
deploy-project consumes the native component/capsule descriptor. deploy-graph
preserves an existing upstream-compiled graph, including its upstream modules.
Credentials are read from the named environment variable, never a CLI key argument.
An uncertain deployment requires receipt reconciliation; it is not replayed.
Local dev reconciliation selects its journal with CONVEX_DOTNET_STATE_DIRECTORY
and the instance name from its retained identity.json using --deployment-identity.

Exit codes: 0 confirmed/recorded/reviewed; 1 invalid input; 2 refused; 3 uncertain.
"""

[<EntryPoint>]
let main arguments =
    try
        match arguments with
        | [|"component";"new";name|] -> Convex.DotNet.Development.ReleaseLocation.createComponent name name; 0
        | [|"component";"new";name;directory|] -> Convex.DotNet.Development.ReleaseLocation.createComponent name directory; 0
        | [|"new";name|] -> Convex.DotNet.Development.ReleaseLocation.create name name; 0
        | [|"new";name;directory|] -> Convex.DotNet.Development.ReleaseLocation.create name directory; 0
        | [|"build";project|] -> Convex.DotNet.Development.ProjectTool.prepare project; 0
        | [|"codegen";project|] | [|"package";project|] -> Convex.DotNet.Development.ProjectTool.package project; 0
        | values when values.Length>1 && values[0]="dev" ->
            Convex.DotNet.Development.LocalDevelopment.options values[1..] |> Convex.DotNet.Development.LocalDevelopment.run
        | [|"watch";project|] -> Convex.DotNet.Development.ProjectTool.watch project; 0
        | values when values.Length>=5 && values[0]="runtime" && values[1]="run" && values[4]="--" ->
            Convex.DotNet.Development.ProjectTool.runtimeRun values[2] values[3] values[5..]
        | [|"runtime";"inspect";distribution|] -> Convex.DotNet.Development.ProjectTool.runtimeInspect distribution; 0
        | [|"runtime";"prepare";distribution;output|] -> Convex.DotNet.Development.ProjectTool.runtimePrepare distribution output; 0
        | [|"env";"list";origin;credential|] -> Convex.DotNet.Development.Operations.environmentList origin credential
        | [|"env";"set";origin;name;value;credential|] -> Convex.DotNet.Development.Operations.environmentSet origin name value credential
        | [|"env";"remove";origin;name;credential|] -> Convex.DotNet.Development.Operations.environmentRemove origin name credential
        | [|"run";origin;kind;address;argumentsFile;credential|] -> Convex.DotNet.Development.Operations.runFunction origin kind address argumentsFile credential
        | [|"logs";origin;cursor;credential|] -> Convex.DotNet.Development.Operations.logs origin cursor credential
        | [|"tables";origin;credential|] -> Convex.DotNet.Development.DataOperations.tables origin credential
        | [|"export";origin;destination;credential|] -> Convex.DotNet.Development.DataOperations.export origin destination credential false
        | [|"export";origin;destination;credential;"--include-storage"|] -> Convex.DotNet.Development.DataOperations.export origin destination credential true
        | [|"import";origin;format;table;path;mode;credential|] -> Convex.DotNet.Development.DataOperations.importFile origin format table path mode credential
        | [|"--help"|] | [|"-h"|] | [|"help"|] ->
            printfn "%s" usage
            0
        | [|"review";directory|] ->
            let prepared=NativeDeployment.prepareDirectory directory "convex"
            for path,digest in NativeDeployment.review prepared do printfn "%s %s" digest path
            0
        | [|"review-project";source|] ->
            let prepared=NativeDeployment.prepareProjectFile source
            for path,digest in NativeDeployment.review prepared do printfn "%s %s" digest path
            0
        | [|("deploy"|"deploy-graph"|"deploy-project" as command);origin;source;credentialVariable|] ->
            let administrator=credential credentialVariable
            let prepared=
                match command with
                | "deploy" -> NativeDeployment.prepareDirectory source "convex"
                | "deploy-project" -> NativeDeployment.prepareProjectFile source
                | _ -> NativeDeployment.prepareGraphFile source
            let graphSha256=NativeDeployment.graphHash prepared
            use journal=new DeploymentJournal.Journal(Uri origin,graphSha256)
            match journal.Prior with
            | DeploymentJournal.Recorded ->
                printfn "%s" (JsonSerializer.Serialize {|outcome="recorded";graphSha256=graphSha256;receipt="priorOwningActivation"|})
                0
            | DeploymentJournal.Refused ->
                printfn "%s" (JsonSerializer.Serialize {|outcome="recordedRefusal";graphSha256=graphSha256|})
                2
            | DeploymentJournal.Uncertain ->
                printfn "%s" (JsonSerializer.Serialize {|outcome="uncertain";graphSha256=graphSha256;redelivery="readOwningDeploymentState";sent=false|})
                3
            | DeploymentJournal.Fresh ->
                match NativeDeployment.activateRecorded (Uri origin) administrator prepared journal.OperationId
                        (fun phase -> journal.Write(phase,DeploymentJournal.JournalOutcome.Sent)) journal.RecordPrepared CancellationToken.None
                        |> fun operation -> operation.GetAwaiter().GetResult() with
                | Confirmed receipt ->
                    journal.Write(DeploymentPhase.Activating,DeploymentJournal.JournalOutcome.Confirmed)
                    printfn "%s" (JsonSerializer.Serialize {|outcome="confirmed";graphSha256=graphSha256;modules=receipt.ModuleHashes |> Array.map(fun (path,digest) -> {|path=path;sha256=digest|})|})
                    0
                | Refused refusal ->
                    journal.Write(refusal.Phase,DeploymentJournal.JournalOutcome.Refused)
                    printfn "%s" (JsonSerializer.Serialize {|outcome="refused";graphSha256=graphSha256;phase=string refusal.Phase;status=refusal.Status;code=refusal.Code|})
                    2
                | Uncertain phase ->
                    journal.Write(phase,DeploymentJournal.JournalOutcome.Uncertain)
                    printfn "%s" (JsonSerializer.Serialize {|outcome="uncertain";graphSha256=graphSha256;phase=string phase;redelivery="readOwningDeploymentState"|})
                    3
        | values when values.Length>=3 && values[0]="reconcile" ->
            let origin,credentialVariable=values[1],values[2]
            let deploymentIdentity =
                match values[3..] with
                | [||] -> None
                | [|"--deployment-identity";identity|] -> Some identity
                | _ -> invalidArg "arguments" "Reconcile accepts only an optional --deployment-identity NAME."
            let administrator=credential credentialVariable
            use journal=new DeploymentJournal.Journal(Uri origin,"",?deploymentIdentity=deploymentIdentity)
            match journal.HistoricalOutcome,journal.PendingToken with
            | Some "confirmed",_ ->
                printfn "%s" (JsonSerializer.Serialize {|outcome="recorded";graphSha256=journal.RecordedGraphSha256 |> Option.toObj;receipt="priorOwningActivation";sent=false|})
                0
            | Some "refused",_ ->
                printfn "%s" (JsonSerializer.Serialize {|outcome="recordedRefusal";graphSha256=journal.RecordedGraphSha256 |> Option.toObj;sent=false|})
                2
            | _,Some expected ->
                match NativeDeployment.readReceipt (Uri origin) administrator expected CancellationToken.None |> fun operation -> operation.GetAwaiter().GetResult() with
                | HistoricalCommit confirmation ->
                    journal.ConfirmHistorical confirmation
                    printfn "%s" (JsonSerializer.Serialize {|outcome="confirmed";graphSha256=journal.RecordedGraphSha256 |> Option.toObj;
                        operationId=confirmation.Token.OperationId;startPushSha256=confirmation.Token.StartPushSha256;
                        commitTimestamp=confirmation.CommitTimestamp;receipt="historicalOwningCommit";sent=false|})
                    0
                | UnresolvedReceipt ->
                    printfn "%s" (JsonSerializer.Serialize {|outcome="uncertain";operationId=expected.OperationId;redelivery="readOwningDeploymentReceipt";sent=false|})
                    3
            | _ ->
                printfn "%s" (JsonSerializer.Serialize {|outcome="uncertain";redelivery="readOwningDeploymentReceipt";sent=false|})
                3
        | _ ->
            eprintfn "%s" usage
            1
    with error ->
        if arguments.Length>0 && ["build";"codegen";"package";"dev";"runtime"] |> List.contains arguments[0] then
            eprintfn "Local development error: %s" error.Message
        // Never print credentials, capsule source or raw provider diagnostics.
        eprintfn "Native deployment input or contract is invalid."
        1
