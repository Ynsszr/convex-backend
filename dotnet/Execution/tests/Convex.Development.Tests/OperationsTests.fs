module Convex.Development.OperationsTests
open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
open Convex.DotNet.Development
open Xunit

let private withServer status response action =
    use socket=new TcpListener(IPAddress.Loopback,0)
    socket.Start()
    let port=(socket.LocalEndpoint :?> IPEndPoint).Port
    socket.Stop()
    use listener=new HttpListener()
    let origin=sprintf "http://127.0.0.1:%d" port
    listener.Prefixes.Add(origin+"/");listener.Start()
    let variable="CONVEX_TEST_"+Guid.NewGuid().ToString("N")
    Environment.SetEnvironmentVariable(variable,"synthetic-admin")
    let receiver=task {
        let! request=listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds 5.)
        use body=new StreamReader(request.Request.InputStream)
        let! contents=body.ReadToEndAsync()
        request.Response.StatusCode<-status
        let bytes=Encoding.UTF8.GetBytes(response:string)
        request.Response.ContentLength64<-int64 bytes.Length
        do! request.Response.OutputStream.WriteAsync(bytes.AsMemory())
        request.Response.Close()
        return contents
    }
    try
        let result=action origin variable
        result,receiver.GetAwaiter().GetResult()
    finally listener.Stop();Environment.SetEnvironmentVariable(variable,null)

[<Fact>]
let ``environment changes send values without putting values in CLI arguments`` () =
    let valueVar="CONVEX_VALUE_"+Guid.NewGuid().ToString("N")
    Environment.SetEnvironmentVariable(valueVar,"test-value")
    try
        let result,body=withServer 200 "{}" (fun origin admin -> Operations.environmentSet origin "TEST_VALUE" valueVar admin)
        Assert.Equal(0,result);Assert.Contains("test-value",body)
    finally Environment.SetEnvironmentVariable(valueVar,null)

[<Fact>]
let ``server errors are not misreported as confirmed operation refusal`` () =
    let result,_=withServer 500 "{}" (fun origin admin -> Operations.environmentRemove origin "TEST_VALUE" admin)
    Assert.Equal(3,result)

[<Fact>]
let ``administrator refusal does not trigger another request`` () =
    let result,_=withServer 403 "{}" (fun origin admin -> Operations.environmentRemove origin "TEST_VALUE" admin)
    Assert.Equal(2,result)

[<Fact>]
let ``unsafe deployment origins fail before credential selection`` () =
    Assert.Throws<ArgumentException>(fun () -> Operations.environmentRemove "http://example.com" "TEST" "ABSENT" |> ignore) |> ignore

[<Fact>]
let ``missing environment values fail before a network request`` () =
    Assert.Throws<ArgumentException>(fun () -> Operations.environmentSet "http://127.0.0.1:3210" "TEST" ("ABSENT_"+Guid.NewGuid().ToString("N")) "ABSENT" |> ignore) |> ignore
