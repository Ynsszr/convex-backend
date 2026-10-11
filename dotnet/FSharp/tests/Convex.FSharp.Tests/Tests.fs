namespace Convex.FSharp.Tests

open System
open Xunit
open Convex.FSharp

module ValueTests =
    [<Fact>]
    let ``Int64 boundary values are within range`` () =
        Assert.Equal(-9223372036854775808L, ConvexConstants.MinInt64)
        Assert.Equal(9223372036854775807L, ConvexConstants.MaxInt64)

    [<Theory>]
    [<InlineData(0L)>]
    [<InlineData(1L)>]
    [<InlineData(-1L)>]
    [<InlineData(42L)>]
    [<InlineData(-9223372036854775808L)>]
    [<InlineData(9223372036854775807L)>]
    let ``Int64 base64 encoding roundtrips correctly`` (v: int64) =
        let encoded = ConvexBase64.encodeInt64 v
        let decoded = ConvexBase64.decodeInt64 encoded
        Assert.Equal(v, decoded)

    [<Fact>]
    let ``Float64 special IEEE values detection and roundtrip`` () =
        Assert.True(ConvexBase64.isSpecialFloat Double.PositiveInfinity)
        Assert.True(ConvexBase64.isSpecialFloat Double.NegativeInfinity)
        Assert.True(ConvexBase64.isSpecialFloat Double.NaN)
        Assert.True(ConvexBase64.isSpecialFloat -0.0)
        Assert.False(ConvexBase64.isSpecialFloat 0.0)
        Assert.False(ConvexBase64.isSpecialFloat 3.14159)

        let posInfEnc = ConvexBase64.encodeFloat64 Double.PositiveInfinity
        let posInfDec = ConvexBase64.decodeFloat64 posInfEnc
        Assert.True(Double.IsPositiveInfinity posInfDec)

        let negInfEnc = ConvexBase64.encodeFloat64 Double.NegativeInfinity
        let negInfDec = ConvexBase64.decodeFloat64 negInfEnc
        Assert.True(Double.IsNegativeInfinity negInfDec)

        let nanEnc = ConvexBase64.encodeFloat64 Double.NaN
        let nanDec = ConvexBase64.decodeFloat64 nanEnc
        Assert.True(Double.IsNaN nanDec)

    [<Fact>]
    let ``Bytes base64 roundtrip`` () =
        let bytes = [| 0uy; 1uy; 255uy; 128uy; 42uy |]
        let encoded = ConvexBase64.encodeBytes bytes
        let decoded = ConvexBase64.decodeBytes encoded
        Assert.Equal<byte>(bytes, decoded)

    [<Theory>]
    [<InlineData("validField", true)>]
    [<InlineData("valid_field_123", true)>]
    [<InlineData("_id", true)>]
    [<InlineData("_creationTime", true)>]
    [<InlineData("_invalidCustom", false)>]
    [<InlineData("", false)>]
    [<InlineData("$system", false)>]
    let ``Field name validation checks user fields`` (name: string, shouldBeValid: bool) =
        let isValid =
            match FieldValidation.validateFieldName name with
            | Ok () -> true
            | Error _ -> false
        Assert.Equal(shouldBeValid, isValid)

    [<Fact>]
    let ``ConvexValue JSON wire serialization matches Convex specification`` () =
        let nullJson = ConvexJson.toJson ConvexValue.Null
        Assert.Equal("null", nullJson)

        let boolJson = ConvexJson.toJson (ConvexValue.Boolean true)
        Assert.Equal("true", boolJson)

        let intJson = ConvexJson.toJson (ConvexValue.Int64 42L)
        Assert.Contains("$integer", intJson)

        let strJson = ConvexJson.toJson (ConvexValue.String "hello \"world\"")
        Assert.Equal("\"hello \\\"world\\\"\"", strJson)

        let specialFloatJson = ConvexJson.toJson (ConvexValue.Float64 Double.PositiveInfinity)
        Assert.Contains("$float", specialFloatJson)

        let normalFloatJson = ConvexJson.toJson (ConvexValue.Float64 3.5)
        Assert.Equal("3.5", normalFloatJson)

module ValidatorTests =
    [<Fact>]
    let ``Primitive validators produce correct JSON AST`` () =
        Assert.Equal("{\"type\":\"null\"}", (V.``null`` ()).ToJson())
        Assert.Equal("{\"type\":\"string\"}", V.string().ToJson())
        Assert.Equal("{\"type\":\"number\"}", V.number().ToJson())
        Assert.Equal("{\"type\":\"bigint\"}", V.int64().ToJson())
        Assert.Equal("{\"type\":\"boolean\"}", V.boolean().ToJson())
        Assert.Equal("{\"type\":\"bytes\"}", V.bytes().ToJson())
        Assert.Equal("{\"type\":\"any\"}", V.any().ToJson())
        Assert.Equal("{\"type\":\"id\",\"tableName\":\"users\"}", (V.id "users").ToJson())

    [<Fact>]
    let ``Composite validators produce correct JSON AST`` () =
        let arrVal = V.array (V.string())
        Assert.Equal("{\"type\":\"array\",\"value\":{\"type\":\"string\"}}", arrVal.ToJson())

        let objVal = V.object [
            "name", V.string()
            "age", V.number()
        ]
        let json = objVal.ToJson()
        Assert.Contains("\"type\":\"object\"", json)
        Assert.Contains("\"name\":{\"fieldType\":{\"type\":\"string\"},\"optional\":false}", json)
        Assert.Contains("\"age\":{\"fieldType\":{\"type\":\"number\"},\"optional\":false}", json)

    [<Fact>]
    let ``Union and nullable validators produce correct JSON AST`` () =
        let nullableStr = V.nullable (V.string())
        let json = nullableStr.ToJson()
        Assert.Equal("{\"type\":\"union\",\"value\":[{\"type\":\"string\"},{\"type\":\"null\"}]}", json)

module SchemaTests =
    type UserDoc = { Name: string; Age: int }

    [<Fact>]
    let ``Schema definition exports expected schema structure`` () =
        let userTable =
            TableSchema.define<UserDoc>("users")
                .WithIndex("by_name", [ "Name" ])
                .WithSearchIndex("search_name", "Name", [ "Age" ])

        let schema =
            Schema.define [
                userTable
            ]

        let exportedJson = schema.Export()
        Assert.Contains("\"tables\":[", exportedJson)
        Assert.Contains("\"tableName\":\"users\"", exportedJson)
        Assert.Contains("\"indexes\":[", exportedJson)
        Assert.Contains("\"indexDescriptor\":\"by_name\"", exportedJson)
        Assert.Contains("\"searchIndexes\":[", exportedJson)
        Assert.Contains("\"indexDescriptor\":\"search_name\"", exportedJson)

module ReflectionTests =
    type Status =
        | Active
        | Inactive of reason: string
        | Pending of code: int64 * note: string

    type UserProfile =
        {
            Username: string
            Bio: string option
            Status: Status
            Score: int64
            Data: byte[] option
        }

    [<Fact>]
    let ``TypeDerivation derives correct schema validator for records and DUs`` () =
        let v = TypeDerivation.derive<UserProfile>()
        let json = v.ToJson()

        // Verify username is required string
        Assert.Contains("\"Username\":{\"fieldType\":{\"type\":\"string\"},\"optional\":false}", json)
        // Verify Bio is optional string
        Assert.Contains("\"Bio\":{\"fieldType\":{\"type\":\"string\"},\"optional\":true}", json)
        // Verify Score is bigint
        Assert.Contains("\"Score\":{\"fieldType\":{\"type\":\"bigint\"},\"optional\":false}", json)
        // Verify Status is union of Active, Inactive, Pending
        Assert.Contains("\"kind\":{\"fieldType\":{\"type\":\"literal\",\"value\":\"Active\"}", json)
        Assert.Contains("\"kind\":{\"fieldType\":{\"type\":\"literal\",\"value\":\"Inactive\"}", json)
        Assert.Contains("\"kind\":{\"fieldType\":{\"type\":\"literal\",\"value\":\"Pending\"}", json)

module ErrorTests =
    type CustomErrorData = { Code: string; Message: string }

    [<Fact>]
    let ``ConvexError holds data and formats message`` () =
        let strErr = ConvexError.create "Something went wrong"
        Assert.Equal("Something went wrong", strErr.Data)
        Assert.Contains("Something went wrong", strErr.Message)

        let customData = { Code = "NOT_FOUND"; Message = "Item not found" }
        let customErr = ConvexError.create customData
        Assert.Equal("NOT_FOUND", customErr.Data.Code)

module HttpTests =
    type UserPayload = { Username: string; Role: string }

    [<Fact>]
    let ``Http helper creates formatted HttpResponse`` () =
        let res = Http.ok "Hello, Convex!"
        Assert.Equal(200, res.Status)
        Assert.Equal("Hello, Convex!", res.Body)
        Assert.Contains(("Content-Type", "text/plain; charset=utf-8"), res.Headers)

        let notFoundRes = Http.notFound "Resource does not exist"
        Assert.Equal(404, notFoundRes.Status)
        Assert.Equal("Resource does not exist", notFoundRes.Body)

        let user = { Username = "alice"; Role = "admin" }
        let jsonRes = Http.json user
        Assert.Equal(200, jsonRes.Status)
        Assert.Contains("\"Username\":\"alice\"", jsonRes.Body)
        Assert.Contains("\"Role\":\"admin\"", jsonRes.Body)

module StorageTests =
    [<Fact>]
    let ``FileMetadata record instantiates correctly`` () =
        let meta: FileMetadata = {
            StorageId = "synthetic-storage-id"
            Size = 1048576L
            ContentType = Some "image/png"
            Sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        }
        Assert.Equal(1048576L, meta.Size)
        Assert.Equal(Some "image/png", meta.ContentType)
        Assert.Equal(64, meta.Sha256.Length)

module ComponentTests =
    [<Fact>]
    let ``Component reference generates correct component targets`` () =
        let counter = Component.create "counter"
        let incMut = counter.Mutation<unit, int64>("functions:increment", Contract.arguments<unit>(), Contract.derived<int64>())
        Assert.Equal("counter:functions:increment", incMut.Name)
        let targetObj = incMut.Address
        Assert.Equal("_reference/childComponent/counter/functions/increment", string targetObj)

    [<Fact>]
    let ``Nested subcomponent generates hierarchical component path`` () =
        let parent = Component.create "parent"
        let child = parent.SubComponent "child"
        let fn = child.Query<unit, string>("api:status", Contract.arguments<unit>(), Contract.derived<string>())
        Assert.Equal("parent/child:api:status", fn.Name)
        let targetObj = fn.Address
        Assert.Equal("_reference/childComponent/parent/child/api/status", string targetObj)

    [<Fact>]
    let ``FunctionHandle roundtrips value and can be converted to FunctionReference`` () =
        let handle = FunctionReference.publicMutation "functions:counter" (Contract.arguments<unit>()) (Contract.derived<int64>())
                     |> FunctionReference.withHandle "function://test;functions:counter"
        Assert.Equal("function://test;functions:counter", handle.Value)
        Assert.Equal("function://test;functions:counter", handle.ToString())
        let fnRef = FunctionReference.ofHandle handle
        Assert.Equal("functions:counter", fnRef.Name)
        Assert.Equal("function://test;functions:counter", string (fnRef.Address))

    [<Fact>]
    let ``A typed public query reference retains its local target`` () =
        let fnRef = FunctionReference.publicQuery "functions:myFunc" (Contract.arguments<unit>()) (Contract.derived<unit>())
        Assert.Equal("functions:myFunc", fnRef.Name)
        Assert.Equal("functions:myFunc", string (fnRef.Address))

module TableIdentityTests =
    type User = { Username: string }
    type AppUserMarker = class end
    type Document = { Title: string }
    type UnregisteredType = { Foo: string }
    type MarkerTable = class end

    [<Fact>]
    let ``Table.define registers exact table name without pluralization guessing`` () =
        let userTable = Table.define<User> "custom_users_table"
        Assert.Equal("custom_users_table", userTable.Name)
        Assert.Equal("custom_users_table", Table.name<User>())
        Assert.Equal("custom_users_table", TableRegistry.getName typeof<User>)

    [<Fact>]
    let ``Table.defineWithMarker registers both document type and marker type`` () =
        let docTable = Table.defineWithMarker<MarkerTable, Document> "documents"
        Assert.Equal("documents", docTable.Name)
        Assert.Equal("documents", Table.name<Document>())
        Assert.Equal("documents", Table.name<MarkerTable>())

    [<Fact>]
    let ``Id<'Table> validator derivation produces exact registered table name`` () =
        let _ = Table.defineWithMarker<AppUserMarker, User> "app_users"
        let validator = TypeDerivation.derive<Id<AppUserMarker>>()
        Assert.Equal("{\"type\":\"id\",\"tableName\":\"app_users\"}", validator.ToJson())

    [<Fact>]
    let ``Unregistered type used in Id<'Table> raises descriptive error`` () =
        let ex = Assert.Throws<Exception>(fun () ->
            TypeDerivation.derive<Id<UnregisteredType>>() |> ignore
        )
        Assert.Contains("Table name is not defined for type 'UnregisteredType'", ex.Message)
        Assert.Contains("Register it using Table.define<UnregisteredType>", ex.Message)

    [<Fact>]
    let ``Compile-time type safety distinguishes IDs of different tables`` () =
        let userId: Id<User> = Id.ofString "user_123"
        let docId: Id<Document> = Id.ofString "doc_456"

        // Type safety: helper function accepting only Id<User>
        let processUser (id: Id<User>) = Id.value id
        let processed = processUser userId
        Assert.Equal("user_123", processed)

        // Compile-time assertion: userId and docId have different types
        Assert.NotEqual(typeof<Id<User>>, typeof<Id<Document>>)

module AdversarialReflectionTests =
    type User = { Name: string }
    type GenericContainer<'T> = { Label: string; Item: 'T }
    type SingleCaseUnion = SingleCase of payload: string
    type ColorEnum = Red = 0 | Green = 1 | Blue = 2

    type NestedDU =
        | Leaf of value: int64
        | Node of left: NestedDU * right: NestedDU

    type ContainerWithNested =
        {
            Title: string
            Tags: string list
            Tree: NestedDU
            Meta: Map<string, int64>
            OptInArr: string option list
            VOptField: ValueOption<string>
            Pair: int * string
        }

    type UnsupportedDateTimeDoc = { Created: DateTime }
    type UnsupportedDecimalDoc = { Price: decimal }
    type UnsupportedGuidDoc = { SessionId: Guid }
    type InvalidFieldNameDoc = { _customField: string }

    [<Fact>]
    let ``Recursive validators fail explicitly rather than weakening children to any`` () =
        let ex = Assert.Throws<Exception>(fun () -> TypeDerivation.derive<NestedDU>() |> ignore)
        Assert.Contains("Recursive type", ex.Message)
        Assert.Contains("ConvexValue", ex.Message)

    [<Fact>]
    let ``Generic records derive closed type validators`` () =
        let v = TypeDerivation.derive<GenericContainer<string>>()
        let json = v.ToJson()
        Assert.Contains("\"Item\":{\"fieldType\":{\"type\":\"string\"}", json)

        let _ = Table.define<User> "adversarial_users"
        let vId = TypeDerivation.derive<GenericContainer<Id<User>>>()
        let jsonId = vId.ToJson()
        Assert.Contains("\"Item\":{\"fieldType\":{\"type\":\"id\",\"tableName\":\"adversarial_users\"}", jsonId)

    [<Fact>]
    let ``Single-case union unwraps directly without redundant multi-union`` () =
        let v = TypeDerivation.derive<SingleCaseUnion>()
        let json = v.ToJson()
        Assert.StartsWith("{\"type\":\"object\"", json)
        Assert.Contains("\"kind\":{\"fieldType\":{\"type\":\"literal\",\"value\":\"SingleCase\"}", json)
        Assert.Contains("\"payload\":{\"fieldType\":{\"type\":\"string\"}", json)

    [<Fact>]
    let ``Enums derive declared literal validators`` () =
        let v = TypeDerivation.derive<ColorEnum>()
        Assert.Equal(Validator.Union [Validator.LiteralFloat 0.; Validator.LiteralFloat 1.; Validator.LiteralFloat 2.], v)

    [<Fact>]
    let ``Tuples derive array validator with element types`` () =
        // Homogeneous tuple
        let vHom = TypeDerivation.derive<int * int>()
        Assert.Equal("{\"type\":\"array\",\"value\":{\"type\":\"number\"}}", vHom.ToJson())

        // Heterogeneous tuple
        let vHet = TypeDerivation.derive<int * string>()
        let json = vHet.ToJson()
        Assert.Contains("\"type\":\"array\"", json)
        Assert.Contains("\"type\":\"union\"", json)

    [<Fact>]
    let ``Maps derive Convex record validator`` () =
        let v = TypeDerivation.derive<Map<string, int64>>()
        let json = v.ToJson()
        Assert.Equal("{\"type\":\"record\",\"keys\":{\"type\":\"string\"},\"values\":{\"fieldType\":{\"type\":\"bigint\"},\"optional\":false}}", json)

    [<Fact>]
    let ``Unsupported DateTime fails with fast descriptive error`` () =
        let ex = Assert.Throws<Exception>(fun () ->
            TypeDerivation.derive<UnsupportedDateTimeDoc>() |> ignore
        )
        Assert.Contains("DateTime", ex.Message)
        Assert.Contains("cannot be automatically derived as a Convex validator", ex.Message)

    [<Fact>]
    let ``Unsupported Decimal fails with fast descriptive error`` () =
        let ex = Assert.Throws<Exception>(fun () ->
            TypeDerivation.derive<UnsupportedDecimalDoc>() |> ignore
        )
        Assert.Contains("Decimal", ex.Message)
        Assert.Contains("cannot be automatically derived as a Convex validator", ex.Message)

    [<Fact>]
    let ``Unsupported Guid fails with fast descriptive error`` () =
        let ex = Assert.Throws<Exception>(fun () ->
            TypeDerivation.derive<UnsupportedGuidDoc>() |> ignore
        )
        Assert.Contains("Guid", ex.Message)
        Assert.Contains("cannot be automatically derived as a Convex validator", ex.Message)

    [<Fact>]
    let ``Invalid record field names starting with underscore are rejected`` () =
        let ex = Assert.Throws<Exception>(fun () ->
            TypeDerivation.derive<InvalidFieldNameDoc>() |> ignore
        )
        Assert.Contains("Invalid record field name", ex.Message)
        Assert.Contains("_customField", ex.Message)

module AdversarialCodecTests =
    type User = { Name: string }

    [<Fact>]
    let ``Tuples encode to arrays and decode back`` () =
        let pair = (42, "hello")
        let encoded = Codec.encode pair
        let decoded = Codec.decode<int * string> encoded
        Assert.Equal(42, fst decoded)
        Assert.Equal("hello", snd decoded)

    [<Fact>]
    let ``Options inside arrays encode None to null instead of undefined`` () =
        let optionsList: string option list = [ None; Some "first"; None; Some "second" ]
        let encoded = Codec.encode optionsList
        let items = encoded :?> obj[]
        Assert.Null(items.[0])
        Assert.Equal("first", unbox<string> items.[1])
        Assert.Null(items.[2])
        Assert.Equal("second", unbox<string> items.[3])

        let decoded = Codec.decode<string option list> encoded
        Assert.Equal<string option list>(optionsList, decoded)

    [<Fact>]
    let ``ValueOption roundtrips ValueSome and ValueNone`` () =
        let vSome = ValueSome "test"
        let encSome = Codec.encode vSome
        let decSome = Codec.decode<ValueOption<string>> encSome
        Assert.Equal(ValueSome "test", decSome)

        let vNone: ValueOption<string> = ValueNone
        let encNone = Codec.encode vNone
        let decNone = Codec.decode<ValueOption<string>> encNone
        Assert.Equal(ValueNone, decNone)

module TypedIndexTests =
    type Article = { Title: string; Body: string; Category: string; Views: int64 }
    type Author = { Name: string }
    type SchemaArticleMarker = class end

    [<Fact>]
    let ``Table index definitions generate strongly typed index descriptors`` () =
        let articles = Table.define<Article> "articles"
        let byCat = articles.Index("by_category", [ "Category"; "Views" ])
        let searchBody = articles.SearchIndex("search_body", "Body", [ "Category" ])
        let byEmbedding = articles.VectorIndex("by_embedding", "Embedding", 1536)

        Assert.Equal("articles", byCat.TableName)
        Assert.Equal("by_category", byCat.IndexName)
        Assert.Equal<string>([ "Category"; "Views" ], byCat.Fields)

        Assert.Equal("articles", searchBody.TableName)
        Assert.Equal("search_body", searchBody.IndexName)
        Assert.Equal("Body", searchBody.SearchField)

        Assert.Equal("articles", byEmbedding.TableName)
        Assert.Equal("by_embedding", byEmbedding.IndexName)
        Assert.Equal(1536, byEmbedding.Dimensions)

    [<Fact>]
    let ``TableDefinition in schema accepts typed index descriptors`` () =
        let articles = Table.defineWithMarker<SchemaArticleMarker, Article> "articles_schema_test"
        let byCat = articles.Index("by_category", [ "Category" ])
        let searchBody = articles.SearchIndex("search_body", "Body", [ "Category" ])
        let byEmbedding = articles.VectorIndex("by_embedding", "Embedding", 1536)

        let tableDef =
            TableSchema.defineForTable(articles)
                .WithIndex(byCat)
                .WithSearchIndex(searchBody)
                .WithVectorIndex(byEmbedding)

        Assert.Equal(1, tableDef.Indexes.Length)
        Assert.Equal("by_category", tableDef.Indexes.Head.IndexDescriptor)
        Assert.Equal(1, tableDef.SearchIndexes.Length)
        Assert.Equal("search_body", tableDef.SearchIndexes.Head.IndexDescriptor)
        Assert.Equal(1, tableDef.VectorIndexes.Length)
        Assert.Equal("by_embedding", tableDef.VectorIndexes.Head.IndexDescriptor)

    [<Fact>]
    let ``IndexRangeBuilder methods do not require manual box casting`` () =
        let range =
            IndexRangeBuilder.Empty
                .Eq("Category", "tech")
                .Gte("Views", 100L)
                .Lt("Views", 1000L)

        Assert.Equal(3, range.Ops.Length)

module CronsTests =
    [<Fact>]
    let ``Crons builder exports valid Convex cron definitions`` () =
        let crons = CronJobs.create()
        let dummyRef = NativeFixtures.unitMutation "functions:cleanup"

        crons.Interval("every_30s", IntervalSchedule.Seconds 30, dummyRef, ())
        crons.Hourly("hourly_job", dummyRef, (), minuteUTC = 15)
        crons.Daily("daily_job", 9, dummyRef, (), minuteUTC = 30)
        crons.Weekly("weekly_job", "monday", 10, dummyRef, ())
        crons.Monthly("monthly_job", 1, 12, dummyRef, ())
        crons.Cron("custom_cron", "*/15 * * * *", dummyRef, ())

        let json = crons.Export()
        Assert.Contains("\"every_30s\":{\"name\":\"functions:cleanup\",\"args\":[{}],\"schedule\":{\"type\":\"interval\",\"seconds\":30}}", json)
        Assert.Contains("\"hourly_job\":{\"name\":\"functions:cleanup\",\"args\":[{}],\"schedule\":{\"type\":\"hourly\",\"minuteUTC\":15}}", json)
        Assert.Contains("\"daily_job\":{\"name\":\"functions:cleanup\",\"args\":[{}],\"schedule\":{\"type\":\"daily\",\"hourUTC\":9,\"minuteUTC\":30}}", json)
        Assert.Contains("\"weekly_job\":{\"name\":\"functions:cleanup\",\"args\":[{}],\"schedule\":{\"type\":\"weekly\",\"dayOfWeek\":\"monday\",\"hourUTC\":10}}", json)
        Assert.Contains("\"monthly_job\":{\"name\":\"functions:cleanup\",\"args\":[{}],\"schedule\":{\"type\":\"monthly\",\"day\":1,\"hourUTC\":12}}", json)
        Assert.Contains("\"custom_cron\":{\"name\":\"functions:cleanup\",\"args\":[{}],\"schedule\":{\"type\":\"cron\",\"cron\":\"*/15 * * * *\"}}", json)

module StructuredErrorTests =
    type UserErrorData = { Code: string; RetryAfterMs: int64 }

    [<Fact>]
    let ``ConvexError active pattern matches custom error data`` () =
        let data = { Code = "RATE_LIMITED"; RetryAfterMs = 5000L }
        let err = ConvexError.create data

        let matched =
            match (err :> exn) with
            | ConvexError (d: UserErrorData) -> Some d
            | _ -> None

        Assert.True(matched.IsSome)
        Assert.Equal("RATE_LIMITED", matched.Value.Code)
        Assert.Equal(5000L, matched.Value.RetryAfterMs)

    [<Fact>]
    let ``Errors.failWithConvexError raises ConvexError with typed data`` () =
        let data = { Code = "NOT_FOUND"; RetryAfterMs = 0L }
        let ex = Assert.Throws<ConvexError<UserErrorData>>(fun () ->
            Errors.failWithConvexError<UserErrorData, unit> data
        )
        Assert.Equal("NOT_FOUND", ex.Data.Code)

module CommitTimestampTests =
    type AuditRecord =
        {
            Action: string
            Created: CommitTimestamp
        }

    [<Fact>]
    let ``CommitTimestamp equality and comparison work as expected`` () =
        let p1 = CommitTimestamp.Placeholder
        let p2 = CommitTimestamp.Placeholder
        let v1 = CommitTimestamp.Value 1000L
        let v2 = CommitTimestamp.Value 2000L
        let v3 = CommitTimestamp.Value 1000L

        Assert.Equal(p1, p2)
        Assert.Equal(v1, v3)
        Assert.NotEqual(v1, v2)
        Assert.NotEqual(p1, v1)

        Assert.True(p1 > v1)
        Assert.True(v1 < v2)
        Assert.True(v2 > v1)

    [<Fact>]
    let ``CommitTimestamp reflection derives Validator.CommitTs`` () =
        let v = TypeDerivation.deriveValidator typeof<AuditRecord>
        match v with
        | Validator.Object fieldMap ->
            let createdField = fieldMap.["Created"]
            Assert.Equal(Validator.CommitTs, createdField.FieldType)
        | _ -> Assert.Fail("Expected Object validator")

    [<Fact>]
    let ``CommitTimestamp encodes and decodes properly`` () =
        let placeholderRecord = { Action = "create"; Created = CommitTimestamp.Placeholder }
        let encodedP = Codec.encode placeholderRecord
        let decodedP = Codec.decode<AuditRecord> encodedP
        Assert.Equal(CommitTimestamp.Placeholder, decodedP.Created)

        let valueRecord = { Action = "update"; Created = CommitTimestamp.Value 1727200000000L }
        let encodedV = Codec.encode valueRecord
        let decodedV = Codec.decode<AuditRecord> encodedV
        Assert.Equal(CommitTimestamp.Value 1727200000000L, decodedV.Created)

module TableMarkerTests =
    type UserMarker = interface end
    type UserDoc = { Name: string; Email: string }

    type ArchivedUserMarker = interface end
    type LegacyUserDoc = { Name: string; Email: string }

    [<Fact>]
    let ``Table.defineWithMarker creates table with distinct marker type`` () =
        let users = Table.defineWithMarker<UserMarker, UserDoc> "users"
        let archived = Table.defineWithMarker<ArchivedUserMarker, UserDoc> "archived_users"

        Assert.Equal("users", users.Name)
        Assert.Equal("archived_users", archived.Name)

        let idValidator = users.IdValidator
        Assert.Equal(Validator.Id "users", idValidator)

        let docValidator = users.DocValidator
        match docValidator with
        | Validator.Object fields ->
            let fieldNames = fields |> Map.toList |> List.map fst
            Assert.Contains("_id", fieldNames)
            Assert.Contains("_creationTime", fieldNames)
            Assert.Contains("Name", fieldNames)
            Assert.Contains("Email", fieldNames)
        | _ -> Assert.Fail("Expected Object doc validator")

    [<Fact>]
    let ``Table.define backward compatibility preserves 1-type-arg behavior`` () =
        let users = Table.define<LegacyUserDoc> "users"
        let byName = users.Index("by_name", [ "Name" ])
        Assert.Equal("users", byName.TableName)
        Assert.Equal("by_name", byName.IndexName)

module SystemTableTests =
    [<Fact>]
    let ``SystemTable names match Convex conventions`` () =
        Assert.Equal("_storage", SystemTable.Storage)
        Assert.Equal("_scheduled_functions", SystemTable.ScheduledFunctions)

        Assert.Equal("_storage", SystemTables.storage.Name)
        Assert.Equal("_scheduled_functions", SystemTables.scheduledFunctions.Name)

    [<Fact>]
    let ``StorageDocument round-trips through Codec`` () =
        let doc: StorageDocument =
            {
                _id = Id.ofString "storage_123"
                _creationTime = 1727200000000.0
                Size = 1024L
                Sha256 = "dummy-sha-256"
                ContentType = Some "image/png"
            }
        let encoded = Codec.encode doc
        let decoded = Codec.decode<StorageDocument> encoded
        Assert.Equal(doc._id, decoded._id)
        Assert.Equal(doc.Size, decoded.Size)
        Assert.Equal(doc.Sha256, decoded.Sha256)
        Assert.Equal(doc.ContentType, decoded.ContentType)

module NestedFunctionOptionsTests =
    [<Fact>]
    let ``TransactionLimits and RunQueryOptions serialize to plain objects`` () =
        let limits: TransactionLimits =
            {
                BytesRead = Some 1000000
                BytesWritten = Some 500000
                DatabaseQueries = Some 50
                DocumentsRead = Some 100
                DocumentsWritten = Some 20
                FunctionsScheduled = Some 5
                ScheduledFunctionArgsBytes = Some 1024
            }
        let opts: MutationRunQueryOptions =
            {
                TransactionLimits = Some limits
                UseStaleSnapshot = Some true
            }
        let js = NestedOptionsHelper.serializeMutationRunQueryOptions opts
        Assert.NotNull(js)
        let dict = js :?> System.Collections.Generic.Dictionary<string, obj>
        Assert.Equal(box true, dict.["useStaleSnapshot"])
        Assert.True(dict.ContainsKey("transactionLimits"))

module ConvexValuesTests =
    [<Fact>]
    let ``ConvexValues.compare follows Convex order`` () =
        // Convex official tag order: null (1) < bigint (2) < number (3) < bool (4) < string (5)
        let n = ConvexValue.Null
        let bi = ConvexValue.Int64 1L
        let num = ConvexValue.Float64 1.0
        let b = ConvexValue.Boolean false
        let s = ConvexValue.String "a"

        Assert.True(ConvexValues.compare n bi < 0)
        Assert.True(ConvexValues.compare bi num < 0)
        Assert.True(ConvexValues.compare num b < 0)
        Assert.True(ConvexValues.compare b s < 0)
        Assert.Equal(0, ConvexValues.compare s (ConvexValue.String "a"))

    [<Fact>]
    let ``ConvexValues.size calculates non-zero size for documents`` () =
        let doc = ConvexValue.Object (Map.ofList [ "name", ConvexValue.String "Alice"; "age", ConvexValue.Int64 30L ])
        let size = ConvexValues.documentSize doc
        Assert.True(size > 0)

module BoundaryRegressionTests =
    type OptionalPayload =
        | Payload of note: string option * explicit: OptionalNull<string>
        | Empty

    [<Fact>]
    let ``Union optional fields use the same presence validators as records`` () =
        let validator = TypeDerivation.derive<OptionalPayload>()
        match validator with
        | Validator.Union (Validator.Object fields :: _) ->
            Assert.True(fields.["note"].Optional)
            Assert.Equal(Validator.String, fields.["note"].FieldType)
            Assert.True(fields.["explicit"].Optional)
            Assert.Equal(V.nullable Validator.String, fields.["explicit"].FieldType)
        | _ -> failwith "Expected a union of object validators"

    [<Fact>]
    let ``Unknown and missing union discriminators fail instead of becoming the first case`` () =
        for tag in [ None; Some "FutureCase" ] do
            let value = ObjectCodec.emptyPlainObject()
            tag |> Option.iter (fun name -> JsHelpers.setProperty value "kind" (box name))
            Assert.ThrowsAny<Exception>(fun () -> Codec.decode<OptionalPayload> value |> ignore) |> ignore

    [<Fact>]
    let ``Explicit ConvexValue data uses native representation and preserves unknown fields`` () =
        let value = ConvexValue.Object (Map.ofList [
            "unknown", ConvexValue.Array [ConvexValue.Null; ConvexValue.Int64 Int64.MaxValue; ConvexValue.Float64 1.25]
            "binary", ConvexValue.Bytes [|0uy; 255uy|]
            "flag", ConvexValue.Boolean true
            "text", ConvexValue.String "future setting"
        ])
        let encoded = Codec.encode value
        Assert.False(JsHelpers.hasProperty encoded "kind")
        Assert.True(JsHelpers.hasProperty encoded "unknown")
        Assert.Equal(value, Codec.decode<ConvexValue> encoded)

    [<Fact>]
    let ``Wire JSON escapes keys and all control characters and preserves double precision`` () =
        let value = ConvexValue.Object (Map.ofList [ "quoted\"key", ConvexValue.String (String [|for i in 0..31 -> char i|]); "float", ConvexValue.Float64 1.2345678901234567 ])
        let previous = Globalization.CultureInfo.CurrentCulture
        try
            Globalization.CultureInfo.CurrentCulture <- Globalization.CultureInfo("tr-TR")
            use parsed = System.Text.Json.JsonDocument.Parse(ConvexJson.toJson value)
            Assert.Equal(String [|for i in 0..31 -> char i|], parsed.RootElement.GetProperty("quoted\"key").GetString())
            Assert.Equal(1.2345678901234567, parsed.RootElement.GetProperty("float").GetDouble())
        finally
            Globalization.CultureInfo.CurrentCulture <- previous

module SystemStateRegressionTests =
    [<Theory>]
    [<InlineData("pending")>]
    [<InlineData("inProgress")>]
    [<InlineData("success")>]
    [<InlineData("failed")>]
    [<InlineData("canceled")>]
    let ``System state adapter maps official wire tags without weakening application union decoding`` (tag: string) =
        let state = ObjectCodec.emptyPlainObject()
        JsHelpers.setProperty state "kind" (box tag)
        if tag = "failed" then JsHelpers.setProperty state "error" (box "reason")
        let raw = ObjectCodec.emptyPlainObject()
        for key, value in ["_id", box "scheduled"; "_creationTime", box 1.; "name", box "f"; "args", box [||]; "scheduledTime", box 2.; "state", state] do
            JsHelpers.setProperty raw key value
        let decoded = SystemDocumentCodec.decode<ScheduledFunctionDocument> raw
        let expected = match tag with "pending" -> ScheduledFunctionState.Pending | "inProgress" -> ScheduledFunctionState.InProgress | "success" -> ScheduledFunctionState.Success | "failed" -> ScheduledFunctionState.Failed "reason" | _ -> ScheduledFunctionState.Canceled
        Assert.Equal(expected, decoded.State)
        Assert.Equal(tag, unbox<string> (JsHelpers.getProperty state "kind"))

module IdBoundaryRegressionTests =
    [<Fact>]
    let ``Ids have native string boundaries on dotnet`` () =
        let id: Id<SystemTable.StorageMarker> = Id.ofString "storage-id"
        Assert.Equal(box "storage-id", Codec.encode id)
        Assert.Equal(id, Codec.decode<Id<SystemTable.StorageMarker>> (box "storage-id"))

module RepresentationRegressionTests =
    type PresenceRecord = { Optional: OptionalNull<string>; Nullable: NullOr<string> }

    [<Fact>]
    let ``Dotnet preserves explicit null and missing independently`` () =
        for optional in [OptionalNull.Missing; OptionalNull.Null; OptionalNull.Value "present"] do
            let value = { Optional = optional; Nullable = NullOr.Null }
            let encoded = Codec.encode value
            Assert.Equal(optional <> OptionalNull.Missing, JsHelpers.hasProperty encoded "Optional")
            Assert.True(JsHelpers.hasProperty encoded "Nullable")
            Assert.Null(JsHelpers.getProperty encoded "Nullable")
            Assert.Equal(value, Codec.decode<PresenceRecord> encoded)

    [<Fact>]
    let ``Nested presence wrappers and nonportable containers fail explicitly`` () =
        for t in [typeof<string option option>; typeof<NullOr<string option>>; typeof<OptionalNull<NullOr<string>>>; typeof<System.Collections.Generic.Dictionary<string,int>>; typeof<seq<int>>; typeof<OptionalNull<string> list>; typeof<Map<string,OptionalNull<string>>>; typeof<unit option>; typeof<ConvexValue option>] do
            Assert.ThrowsAny<Exception>(fun () -> TypeDerivation.deriveValidator t |> ignore) |> ignore

    [<Fact>]
    let ``Typed immutable maps roundtrip values and retain None keys as null`` () =
        let value = Map.ofList ["absent", None; "present", Some 9223372036854775807L]
        let encoded = Codec.encode value
        Assert.True(JsHelpers.hasProperty encoded "absent")
        Assert.Null(JsHelpers.getProperty encoded "absent")
        Assert.True((value = Codec.decode<Map<string, int64 option>> encoded))
        let keyed: Map<Id<SystemTable.StorageMarker>, string> = Map.ofList [Id.ofString "storage-key", "value"]
        let keyedEncoded = Codec.encode keyed
        Assert.True(JsHelpers.hasProperty keyedEncoded "storage-key")
        Assert.True((keyed = Codec.decode<Map<Id<SystemTable.StorageMarker>,string>> keyedEncoded))

    [<Theory>]
    [<InlineData(1.5)>]
    [<InlineData(2147483648.)>]
    [<InlineData(-2147483649.)>]
    [<InlineData(Double.NaN)>]
    [<InlineData(Double.PositiveInfinity)>]
    let ``Integral decoders reject fractional nonfinite and overflow values`` (number: float) =
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<int> (box number) |> ignore) |> ignore

    [<Fact>]
    let ``Integral boundaries do not wrap or accept null`` () =
        Assert.Equal(Int32.MaxValue, Codec.decode<int> (box 2147483647.))
        Assert.Equal(UInt32.MaxValue, Codec.decode<uint32> (box 4294967295.))
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<byte> (box 256.) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<uint32> (box -1.) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<int> null |> ignore) |> ignore

module Int64ConversionRegressionTests =
    [<Fact>]
    let ``Int64 number coercion requires exact safe integers`` () =
        Assert.Equal(9007199254740991L, Codec.decode<int64> (box 9007199254740991.))
        for value in [box 1.5; box 9007199254740992.; box Double.NaN; box "42"] do
            Assert.ThrowsAny<Exception>(fun () -> Codec.decode<int64> value |> ignore) |> ignore

module UnsupportedCodecRegressionTests =
    [<Fact>]
    let ``Unsupported containers fail directly at codec boundaries too`` () =
        let value = System.Collections.Generic.Dictionary<string, int>()
        value.Add("key", 1)
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode value |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<System.Collections.Generic.Dictionary<string,int>> (ObjectCodec.emptyPlainObject()) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode (Map.ofList [1,"value"]) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode (Some (null: string)) |> ignore) |> ignore

module CheckedCollectionTests =
    type Mode = Off = 0 | On = 1

    [<Fact>]
    let ``ResizeArray roundtrip retains mutation capability and nested options`` () =
        let source = ResizeArray<string option>([Some "one"; None])
        let decoded = Codec.decode<ResizeArray<string option>>(Codec.encode source)
        decoded.Add(Some "three")
        Assert.Equal<string option list>([Some "one"; None; Some "three"], List.ofSeq decoded)
        Assert.Equal(2, source.Count)

    [<Fact>]
    let ``Tuples reject length and positional type mismatches`` () =
        for raw in [box [|box 1|]; box [|box 1; box "ok"; box 3|]; box [|box "wrong"; box 1|]; box [|box 1; box 2|]; box [|box 1; null|]] do
            Assert.ThrowsAny<Exception>(fun () -> Codec.decode<int * string> raw |> ignore) |> ignore

    [<Fact>]
    let ``Enums reject undeclared and nonintegral values on both boundaries`` () =
        Assert.Equal(Mode.On, Codec.decode<Mode>(box 1.))
        for raw in [box 2.; box 0.5; box "1"; null] do
            Assert.ThrowsAny<Exception>(fun () -> Codec.decode<Mode> raw |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode (enum<Mode> 2) |> ignore) |> ignore

    [<Fact>]
    let ``Collections and primitive codecs reject coercions`` () =
        for raw in [null; box "array"; box 1] do
            Assert.ThrowsAny<Exception>(fun () -> Codec.decode<string list> raw |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<bool>(box "true") |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<string>(box 5) |> ignore) |> ignore


module DatabaseInferenceTests =
    type Document = { Name: string }
    type Marker = Marker

    // These compile without a return annotation: Id-only Get must infer the
    // document from the shorthand identity; separated identity needs its table.
    let private inferredName (db: IDatabaseReader) (id: Id<Document>) = async {
        let! doc = db.Get(id)
        return doc |> Option.map (fun value -> value.Name)
    }
    let private separateName (db: IDatabaseReader) (table: Table<Marker, Document>) (id: Id<Marker>) = async {
        let! doc = db.Get(table, id)
        return doc |> Option.map (fun value -> value.Name)
    }

    [<Fact>]
    let ``Get infers document type from its authoritative typed input`` () =
        Assert.NotNull(box inferredName)
        Assert.NotNull(box separateName)

module TuplePositionTests =
    [<Fact>]
    let ``Tuple nullable and binary positions reject unrelated union members`` () =
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<unit * int>(box [|box 1; box 2|]) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<byte[] * int>(box [|box 1; box 2|]) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<int64 * string>(box [|null; box "text"|]) |> ignore) |> ignore

module IntegralRoundtripTests =
    [<Fact>]
    let ``All supported small integral codecs recognize boxed CLR numbers`` () =
        Assert.Equal(255uy, Codec.decode<byte>(Codec.encode 255uy))
        Assert.Equal(-128y, Codec.decode<sbyte>(Codec.encode -128y))
        Assert.Equal(-32768s, Codec.decode<int16>(Codec.encode -32768s))
        Assert.Equal(65535us, Codec.decode<uint16>(Codec.encode 65535us))
        Assert.Equal(4294967295u, Codec.decode<uint32>(Codec.encode 4294967295u))


module NativeRawBoundaryTests =
    let plain fields =
        let target = ObjectCodec.emptyPlainObject()
        for key,value in fields do JsHelpers.setProperty target key value
        target

    [<Fact>]
    let ``Untyped boundaries snapshot valid objects and omit undefined properties`` () =
        let bytes = [|1uy;2uy|]
        let nested = plain ["future",box [|box Int64.MaxValue;box bytes;null|]]
        let source = plain ["",box 1.;"_custom",nested;"$omitted",JsHelpers.undefinedValue()]
        let result = Codec.decode<obj>(Codec.encode<obj> source)
        Assert.False(JsHelpers.hasProperty result "$omitted")
        JsHelpers.setProperty nested "later" (box true)
        let snapshot = JsHelpers.getProperty result "_custom"
        Assert.False(JsHelpers.hasProperty snapshot "later")
        let array = unbox<obj[]> (JsHelpers.getProperty snapshot "future")
        bytes.[0] <- 9uy
        Assert.Equal(1uy,(unbox<byte[]> array.[1]).[0])

    [<Fact>]
    let ``Untyped boundaries reject unsupported classes and cyclic graphs with paths`` () =
        let cyclic = plain []
        JsHelpers.setProperty cyclic "self" cyclic
        let cycle = Assert.ThrowsAny<Exception>(fun () -> Codec.encode<obj> cyclic |> ignore)
        Assert.Contains("$.self",cycle.Message)
        let arrayCycle = Array.zeroCreate<obj> 1
        arrayCycle.[0] <- box arrayCycle
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<obj>(box arrayCycle) |> ignore) |> ignore
        for value in [box DateTime.UtcNow;box Guid.Empty;box 1M;box [1;2];box {| Value = "domain record" |};JsHelpers.undefinedValue();box [|JsHelpers.undefinedValue()|]] do
            Assert.ThrowsAny<Exception>(fun () -> Codec.encode<obj> value |> ignore) |> ignore

    [<Fact>]
    let ``Raw field validation matches Convex value fields rather than document root rules`` () =
        for name in ["$reserved";"bad\nkey";"é";String.replicate 1025 "x"] do
            Assert.ThrowsAny<Exception>(fun () -> Codec.decode<obj>(plain [name,box true]) |> ignore) |> ignore
        let value = ConvexValue.Object(Map.ofList ["",ConvexValue.String "empty";"_custom",ConvexValue.Int64 1L])
        Assert.Equal(value,Codec.decode<ConvexValue>(Codec.encode value))
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<ConvexValue>(box DateTime.UtcNow) |> ignore) |> ignore

module CompositeBoundaryTests =
    type TextRecord = { Text: string option }
    type CountRecord = { Count: int option }
    type Choice = Text of value: string option | Empty
    type Document = { Name: string }

    let plain = NativeRawBoundaryTests.plain

    [<Fact>]
    let ``Composite tuple positions reject foreign objects rather than dropping fields`` () =
        let valid = (plain ["Text",box "kept"],plain ["Count",box 3])
        Assert.Equal(({Text=Some "kept"},{Count=Some 3}), Codec.decode<TextRecord * CountRecord>(box [|fst valid;snd valid|]))
        for raw in [box [|snd valid;fst valid|];box [|box 1;plain []|];box [|plain ["Text",null];plain []|]] do
            Assert.ThrowsAny<Exception>(fun () -> Codec.decode<TextRecord * CountRecord> raw |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<Map<string,int> * int>(box [|box 1;box 2|]) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<Choice>(plain ["kind",box "Empty";"value",box "extra"]) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<Choice>(plain ["kind",box "Text";"value",null]) |> ignore) |> ignore

    [<Fact>]
    let ``Application records are exact while database and transport projections are explicit`` () =
        let native = plain ["Name",box "kept";"_id",box "id";"_creationTime",box 1.]
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<Document> native |> ignore) |> ignore
        Assert.Equal({Name="kept"},Codec.decodeDocument<Document> native)
        let owner = plain ["name",box "owner";"future",box true]
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<Document> owner |> ignore) |> ignore
        Assert.Equal({Name="owner"},Codec.decodeTransport<Document> owner)
        let foreign = plain ["Name",box "kept";"Future",box true]
        Assert.ThrowsAny<Exception>(fun () -> Codec.decodeDocument<Document> foreign |> ignore) |> ignore

module UnsupportedCodecTests =
    [<Fact>]
    let ``Direct codecs reject types without a derived supported representation`` () =
        for t,value in [typeof<DateTime>,box DateTime.UtcNow;typeof<Guid>,box Guid.Empty;typeof<decimal>,box 1M;typeof<float32>,box 1.f] do
            Assert.ThrowsAny<Exception>(fun () -> Codec.encodeValue t value |> ignore) |> ignore
            Assert.ThrowsAny<Exception>(fun () -> Codec.decodeValue t (box 1.) |> ignore) |> ignore

    [<Fact>]
    let ``Union document projection removes only undeclared system fields`` () =
        let raw = NativeRawBoundaryTests.plain ["kind",box "Text";"value",box "kept";"_id",box "id";"_creationTime",box 1.]
        Assert.Equal(CompositeBoundaryTests.Choice.Text (Some "kept"),Codec.decodeDocument<CompositeBoundaryTests.Choice> raw)


module UnitArgumentBoundaryTests =
    type UnitRecord = { Value: unit }
    type NumberRecord = { Value: int }

    [<Fact>]
    let ``Unit values are null while unit function arguments are empty objects`` () =
        Assert.Null(Codec.encode ())
        Assert.Equal((),Codec.decode<unit> null)
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<unit>(box 1) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<UnitRecord * NumberRecord>(box [|NativeRawBoundaryTests.plain ["Value",box 1];NativeRawBoundaryTests.plain ["Value",null]|]) |> ignore) |> ignore
        let empty = Codec.encodeArguments ()
        Assert.Empty(JsHelpers.objectKeys empty)
        Assert.Equal((),Codec.decodeArguments<unit> empty)
        Assert.ThrowsAny<Exception>(fun () -> Codec.decodeArguments<unit> null |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.encodeArguments 1 |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decodeArguments<unit>(NativeRawBoundaryTests.plain ["unexpected",box 1]) |> ignore) |> ignore

    [<Fact>]
    let ``Typed cron serialization retains arguments and native Convex wire markers`` () =
        let crons = CronJobs.create()
        let args = {| Count = Int64.MaxValue; Bytes = [|0uy;255uy|]; Ratio = infinity; Missing = (None: string option) |}
        crons.Interval("typed", IntervalSchedule.Seconds 30, FunctionReference.internalMutation "functions:target" (Contract.arguments<_>()) (Contract.derived<unit>()), args) |> ignore
        crons.Interval("empty", IntervalSchedule.Seconds 30, NativeFixtures.unitMutation "functions:noargs", ()) |> ignore
        let json = crons.Export()
        Assert.Contains("$integer",json)
        Assert.Contains("$bytes",json)
        Assert.Contains("$float",json)
        Assert.DoesNotContain("Missing",json)
        Assert.Contains("\"args\":[{}]",json)


module RawResourceBoundaryTests =
    open NativeRawBoundaryTests
    let nested depth =
        let mutable value = box 1
        for _ in 1 .. depth do value <- box [|value|]
        value

    [<Fact>]
    let ``Raw field names accept SDK value limit independent of identifier limit`` () =
        for length in [65;1024] do
            let name = String.replicate length "x"
            let encoded = Codec.encode<obj>(plain [name,box true])
            Assert.True(JsHelpers.hasProperty encoded name)
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode<obj>(plain [String.replicate 1025 "x",box true]) |> ignore) |> ignore

    [<Fact>]
    let ``Raw containers enforce upstream depth array and retained field bounds`` () =
        Codec.encode<obj>(nested 64) |> ignore
        let error = Assert.ThrowsAny<Exception>(fun () -> Codec.encode<obj>(nested 65) |> ignore)
        Assert.Contains("64 container",error.Message)
        Codec.encode<obj>(box (Array.create 8192 null)) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode<obj>(box (Array.create 8193 null)) |> ignore) |> ignore
        let fields count = plain [for i in 1 .. count -> string i,box i]
        Codec.encode<obj>(fields 1024) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode<obj>(fields 1025) |> ignore) |> ignore
        let omitted = fields 1024
        for i in 1 .. 10 do JsHelpers.setProperty omitted ("missing"+string i) (JsHelpers.undefinedValue())
        Codec.encode<obj> omitted |> ignore

    [<Fact>]
    let ``Raw total size counts UTF8 aliases and exact bytes before copying`` () =
        Codec.encode<obj>(box (Array.zeroCreate<byte> (33554432-2))) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode<obj>(box (Array.zeroCreate<byte> (33554432-1))) |> ignore) |> ignore
        let shared = box (String.replicate 1048576 "é")
        let error = Assert.ThrowsAny<Exception>(fun () -> Codec.encode<obj>(box (Array.create 16 shared)) |> ignore)
        Assert.Contains("32 MiB",error.Message)
        Assert.Contains("$[15]",error.Message)

    [<Fact>]
    let ``Raw CLR arrays reject nonportable dimensions and lower bounds`` () =
        for value in [box (Array2D.zeroCreate<obj> 1 1);box (Array.CreateInstance(typeof<obj>,[|1|],[|1|]))] do
            Assert.ThrowsAny<Exception>(fun () -> Codec.encode<obj> value |> ignore) |> ignore


module EnumStorageContractTests =
    type Tiny = Low = -128y | High = 127y
    type ByteMode = Zero = 0uy | Max = 255uy
    type ShortMode = Low = -32768s | High = 32767s
    type UnsignedShort = Zero = 0us | Max = 65535us
    type UnsignedMode = Zero = 0u | Max = 4294967295u
    type Wide = Min = -9223372036854775808L | Max = 9223372036854775807L
    [<Flags>]
    type NamedFlags = None = 0 | Read = 1 | Write = 2 | Both = 3 | Alias = 3 | Execute = 4

    [<Fact>]
    let ``Portable enum storage retains exact declared numeric values`` () =
        for t,values in [typeof<Tiny>,[-128.;127.];typeof<ByteMode>,[0.;255.];typeof<ShortMode>,[-32768.;32767.];typeof<UnsignedShort>,[0.;65535.];typeof<UnsignedMode>,[0.;4294967295.]] do
            Assert.Equal(Validator.Union(List.map Validator.LiteralFloat values),TypeDerivation.deriveValidator t)
            for value in values do
                let decoded = Codec.decodeValue t (box value)
                Assert.Equal(t,decoded.GetType())
                Assert.Equal(value,unbox<float>(Codec.encodeValue t decoded))
            for bad in [box -129.;box 1.5;box Double.NaN;box Double.PositiveInfinity;box "0";null] do
                Assert.ThrowsAny<Exception>(fun () -> Codec.decodeValue t bad |> ignore) |> ignore

    [<Fact>]
    let ``Flags accept declared composites aliases and zero but reject unnamed combinations`` () =
        let expected = Validator.Union([0.;1.;2.;3.;4.] |> List.map Validator.LiteralFloat)
        Assert.Equal(expected,TypeDerivation.derive<NamedFlags>())
        Assert.Equal(NamedFlags.Both,Codec.decode<NamedFlags>(box 3.))
        Assert.Equal(box 3.,Codec.encode NamedFlags.Alias)
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode (NamedFlags.Read ||| NamedFlags.Execute) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<NamedFlags>(box 5.) |> ignore) |> ignore

    [<Fact>]
    let ``Wide enum diagnostics fail consistently before optional omission`` () =
        let failures = [ (fun () -> TypeDerivation.derive<Wide option>() |> ignore);(fun () -> Codec.encode<Wide option> None |> ignore);(fun () -> Codec.decode<Wide option> null |> ignore)]
        let messages = failures |> List.map (fun run -> Assert.ThrowsAny<Exception>(run).Message)
        Assert.True(messages |> List.forall ((=) messages.Head))
        Assert.Contains("$type?",messages.Head)
        Assert.Contains("8/16/32",messages.Head)

module CompleteShapePreflightTests =
    type UnsupportedRecord = { Hidden: DateTime option; Empty: Guid list }
    type UnsupportedChoice = Safe | Unsafe of payload: decimal
    type CollidingChoice = Safe | Unsafe of kind: string

    [<Fact>]
    let ``Absent payloads cannot hide unsupported declared codecs`` () =
        let raw = ObjectCodec.emptyPlainObject()
        JsHelpers.setProperty raw "Empty" (box [||])
        let runs = [(fun () -> TypeDerivation.derive<UnsupportedRecord>() |> ignore);(fun () -> Codec.encode {Hidden=None;Empty=[]} |> ignore);(fun () -> Codec.decode<UnsupportedRecord> raw |> ignore)]
        let messages = runs |> List.map (fun run -> Assert.ThrowsAny<Exception>(run).Message)
        Assert.True(messages |> List.forall ((=) messages.Head))
        Assert.Contains("$type.Hidden?",messages.Head)
        for run in [(fun () -> Codec.encode UnsupportedChoice.Safe |> ignore);(fun () -> Codec.encode CollidingChoice.Safe |> ignore)] do
            Assert.ThrowsAny<Exception>(run) |> ignore

    [<Fact>]
    let ``Typed multidimensional arrays reject before empty collection encoding`` () =
        for run in [(fun () -> TypeDerivation.derive<int[,]>() |> ignore);(fun () -> Codec.encode (Array2D.zeroCreate<int> 0 0) |> ignore)] do
            let error = Assert.ThrowsAny<Exception>(run)
            Assert.Contains("one-dimensional",error.Message)


module ValidatorJsonBoundaryTests =
    [<Fact>]
    let ``Validator numeric literal JSON retains precision independent of culture`` () =
        let prior = Globalization.CultureInfo.CurrentCulture
        try
            Globalization.CultureInfo.CurrentCulture <- Globalization.CultureInfo("fr-FR")
            for value in [4294967295.;2147483647.;0.12345678901234567;Double.MaxValue] do
                use doc = System.Text.Json.JsonDocument.Parse((Validator.LiteralFloat value).ToJson())
                Assert.Equal(value,doc.RootElement.GetProperty("value").GetDouble())
        finally Globalization.CultureInfo.CurrentCulture <- prior

    [<Fact>]
    let ``Validator strings and object names escape every JSON control character`` () =
        let literal = "quote\"slash\\" + String(Array.init 32 char)
        use doc = System.Text.Json.JsonDocument.Parse((Validator.LiteralString literal).ToJson())
        Assert.Equal(literal,doc.RootElement.GetProperty("value").GetString())
        let key = "a\"b\\c"
        let validator = Validator.Object(Map.ofList [key,{FieldType=Validator.String;Optional=false}])
        use obj = System.Text.Json.JsonDocument.Parse(validator.ToJson())
        Assert.True(obj.RootElement.GetProperty("value").TryGetProperty(key) |> fst)

module TypedResourceContractTests =
    type Tree = Leaf | Branch of child: Tree
    type MutableTree = { mutable Child: MutableTree option }
    type BytesEnvelope = { Payload: byte[] }

    [<Fact>]
    let ``Recursive typed values enforce exact wire depth without weakening validators`` () =
        let tree depth = [1..depth-1] |> List.fold (fun child _ -> Branch child) Leaf
        let encoded = Codec.encode (tree 64)
        Assert.Equal(tree 64,Codec.decode<Tree> encoded)
        Assert.Contains("64 container",Assert.ThrowsAny<Exception>(fun () -> Codec.encode (tree 65) |> ignore).Message)
        Assert.Contains("Recursive type",Assert.ThrowsAny<Exception>(fun () -> TypeDerivation.derive<Tree>() |> ignore).Message)

    [<Fact>]
    let ``Typed cycles reject while repeated shared records roundtrip`` () =
        let node = {Child=None}
        node.Child <- Some node
        Assert.Contains("Cyclic typed",Assert.ThrowsAny<Exception>(fun () -> Codec.encode node |> ignore).Message)
        let shared = {Child=None}
        let result = Codec.decode<MutableTree array>(Codec.encode [|shared;shared|])
        Assert.Equal(2,result.Length)
        Assert.True(result |> Array.forall(fun x -> x.Child.IsNone))
        let raw = ObjectCodec.emptyPlainObject()
        JsHelpers.setProperty raw "Child" raw
        Assert.Contains("Cyclic",Assert.ThrowsAny<Exception>(fun () -> Codec.decode<MutableTree> raw |> ignore).Message)

    [<Fact>]
    let ``Typed arrays maps and bytes obey upstream raw resource limits`` () =
        Assert.Equal(8192,Codec.decode<int array>(Codec.encode (Array.zeroCreate<int> 8192)).Length)
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode (Array.zeroCreate<int> 8193) |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<int array>(box (Array.zeroCreate<int> 8193)) |> ignore) |> ignore
        let fields count = [for i in 1..count -> string i,i] |> Map.ofList
        Assert.Equal(1024,Codec.decode<Map<string,int>>(Codec.encode (fields 1024)).Count)
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode (fields 1025) |> ignore) |> ignore
        let maxBytes = Array.zeroCreate<byte> (33554432-2)
        Assert.Equal(maxBytes.Length,(Codec.encode maxBytes :?> byte[]).Length)
        Assert.ThrowsAny<Exception>(fun () -> Codec.encode {Payload=maxBytes} |> ignore) |> ignore
        let raw = ObjectCodec.emptyPlainObject()
        JsHelpers.setProperty raw "Payload" (box maxBytes)
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<BytesEnvelope> raw |> ignore) |> ignore

module ConsolidatedBoundaryTests =
    type Owner = class end
    type Shared = { Value: string }
    type FirstOwner = class end
    type SecondOwner = class end
    type Command = SetValue of value: string | ClearValue

    [<Fact>]
    let ``An exact table identity cannot be rebound`` () =
        Table.register<Owner> "consolidated_owner"
        Table.register<Owner> "consolidated_owner"
        let error = Assert.Throws<InvalidOperationException>(fun () -> Table.register<Owner> "different_owner")
        Assert.Contains("cannot be rebound", error.Message)
        Assert.Equal("consolidated_owner", Table.name<Owner>())

    [<Fact>]
    let ``Shared document shapes require explicit owning markers`` () =
        let first = Table.defineWithMarker<FirstOwner, Shared> "consolidated_first"
        let second = Table.defineWithMarker<SecondOwner, Shared> "consolidated_second"
        Assert.Equal(Validator.Id first.Name, TypeDerivation.derive<Id<FirstOwner>>())
        Assert.Equal(Validator.Id second.Name, TypeDerivation.derive<Id<SecondOwner>>())
        Assert.Throws<InvalidOperationException>(fun () -> Table.name<Shared>() |> ignore) |> ignore

    [<Fact>]
    let ``Document projection preserves metadata and rejects malformed metadata`` () =
        let raw = JsHelpers.createSimpleObject()
        JsHelpers.setProperty raw "_id" (box "document-id")
        JsHelpers.setProperty raw "_creationTime" (box 123.)
        JsHelpers.setProperty raw "Value" (box "content")
        let document = Doc.decode<Owner, Shared> raw
        Assert.Equal("document-id", Id.value document.Id)
        Assert.Equal(123., document.CreationTime)
        Assert.Equal("content", document.Data.Value)
        for invalid in [box "123"; box Double.NaN; box Double.PositiveInfinity] do
            JsHelpers.setProperty raw "_creationTime" invalid
            Assert.Throws<InvalidOperationException>(fun () -> Doc.decode<Owner, Shared> raw |> ignore) |> ignore
        JsHelpers.setProperty raw "_creationTime" (box 123.)
        JsHelpers.setProperty raw "_id" (box "")
        Assert.Throws<InvalidOperationException>(fun () -> Doc.decode<Owner, Shared> raw |> ignore) |> ignore

    [<Fact>]
    let ``Function registration rejects scalar arguments and accepts object unions`` () =
        Assert.Throws<ArgumentException>(fun () -> Convex.query (fun _ (value:int) -> async { return value }) |> ignore) |> ignore
        match (Contract.arguments<Command>()).Validator with
        | Validator.Union _ -> ()
        | _ -> Assert.Fail("Object-envelope commands must retain their union validator")
        Convex.queryTask (fun _ (value:Shared) -> task { return value.Value }) |> ignore
