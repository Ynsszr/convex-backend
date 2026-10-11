namespace Convex.FSharp

open System

type DbIndex = { IndexDescriptor:string; Fields:string list }
type SearchIndex = { IndexDescriptor:string; SearchField:string; FilterFields:string list }
type VectorIndex = { IndexDescriptor:string; VectorField:string; Dimensions:int; FilterFields:string list }

module SchemaNames =
    let unique owner names =
        let mutable seen=Set.empty
        for name in names do
            if String.IsNullOrWhiteSpace name || name.Trim()<>name then
                invalidArg "name" ("An exact name is required in " + owner)
            if seen.Contains name then
                invalidArg "name" (sprintf "Name '%s' has more than one declaration in %s" name owner)
            seen<-seen.Add name

/// An immutable witness to an active declaration in a supplied schema owner.
/// It does not establish that the index is enabled in a deployed database.
[<Sealed>]
type DeclaredTableIndex<'Table,'Doc> internal (tableName:string,indexName:string,fields:string list) =
    member _.TableName = tableName
    member _.IndexName = indexName
    member _.Fields = fields

[<Sealed>]
type DeclaredTableSearchIndex<'Table,'Doc> internal (tableName:string,indexName:string,searchField:string,filterFields:string list) =
    member _.TableName = tableName
    member _.IndexName = indexName
    member _.SearchField = searchField
    member _.FilterFields = filterFields

[<Sealed>]
type DeclaredTableVectorIndex<'Table,'Doc> internal (tableName:string,indexName:string,vectorField:string,dimensions:int,filterFields:string list) =
    member _.TableName = tableName
    member _.IndexName = indexName
    member _.VectorField = vectorField
    member _.Dimensions = dimensions
    member _.FilterFields = filterFields

/// Active and staged definitions remain separate, exactly as in the owning SDK.
type TableDefinition =
    { TableName:string
      DocumentValidator:Validator
      Indexes:DbIndex list
      SearchIndexes:SearchIndex list
      VectorIndexes:VectorIndex list
      StagedIndexes:DbIndex list
      StagedSearchIndexes:SearchIndex list
      StagedVectorIndexes:VectorIndex list
      StagedDocumentValidator:Validator option }
    member this.Name = this.TableName
    member this.IndexNames =
        [ for index in this.Indexes @ this.StagedIndexes -> index.IndexDescriptor ] @
        [ for index in this.SearchIndexes @ this.StagedSearchIndexes -> index.IndexDescriptor ] @
        [ for index in this.VectorIndexes @ this.StagedVectorIndexes -> index.IndexDescriptor ]
    member private this.CheckIndex(name:string) = SchemaNames.unique this.TableName (this.IndexNames @ [name])
    member private this.CheckOwner(tableName:string) =
        if tableName<>this.TableName then invalidArg "index" "An index descriptor belongs to another table"
    member this.WithIndex(name:string, fields:string list, ?staged:bool) =
        this.CheckIndex name
        let index={IndexDescriptor=name; Fields=fields}
        if defaultArg staged false then {this with StagedIndexes=this.StagedIndexes @ [index]}
        else {this with Indexes=this.Indexes @ [index]}
    member this.WithIndex(index:TableIndex<'Table,'Doc>, ?staged:bool) =
        this.CheckOwner index.TableName
        this.WithIndex(index.IndexName,index.Fields,?staged=staged)
    member this.WithSearchIndex(name:string, searchField:string, ?filterFields:string list, ?staged:bool) =
        this.CheckIndex name
        let index={IndexDescriptor=name;SearchField=searchField;FilterFields=defaultArg filterFields []}
        if defaultArg staged false then {this with StagedSearchIndexes=this.StagedSearchIndexes @ [index]}
        else {this with SearchIndexes=this.SearchIndexes @ [index]}
    member this.WithSearchIndex(index:TableSearchIndex<'Table,'Doc>, ?staged:bool) =
        this.CheckOwner index.TableName
        this.WithSearchIndex(index.IndexName,index.SearchField,index.FilterFields,?staged=staged)
    member this.WithVectorIndex(name:string, vectorField:string, dimensions:int, ?filterFields:string list, ?staged:bool) =
        this.CheckIndex name
        let index={IndexDescriptor=name;VectorField=vectorField;Dimensions=dimensions;FilterFields=defaultArg filterFields []}
        if defaultArg staged false then {this with StagedVectorIndexes=this.StagedVectorIndexes @ [index]}
        else {this with VectorIndexes=this.VectorIndexes @ [index]}
    member this.WithVectorIndex(index:TableVectorIndex<'Table,'Doc>, ?staged:bool) =
        this.CheckOwner index.TableName
        this.WithVectorIndex(index.IndexName,index.VectorField,index.Dimensions,index.FilterFields,?staged=staged)
    /// Declaration proposals can be used to author schemas; only active exact
    /// declarations produce witnesses accepted by checked runtime overloads.
    member this.BindIndex(table:Table<'Table,'Doc>,index:TableIndex<'Table,'Doc>) =
        SchemaNames.unique this.TableName this.IndexNames
        this.CheckOwner table.Name
        this.CheckOwner index.TableName
        let declared = this.Indexes |> List.tryFind(fun value -> value.IndexDescriptor=index.IndexName)
                       |> Option.defaultWith(fun () -> invalidArg "index" "An active database index declaration is required")
        if index.Fields<>declared.Fields then invalidArg "index" "Index fields differ from the canonical declaration"
        DeclaredTableIndex<'Table,'Doc>(this.TableName,declared.IndexDescriptor,declared.Fields)
    member this.BindSearchIndex(table:Table<'Table,'Doc>,index:TableSearchIndex<'Table,'Doc>) =
        SchemaNames.unique this.TableName this.IndexNames
        this.CheckOwner table.Name
        this.CheckOwner index.TableName
        let declared = this.SearchIndexes |> List.tryFind(fun value -> value.IndexDescriptor=index.IndexName)
                       |> Option.defaultWith(fun () -> invalidArg "index" "An active search index declaration is required")
        if index.SearchField<>declared.SearchField || index.FilterFields<>declared.FilterFields then
            invalidArg "index" "Search metadata differs from the canonical declaration"
        DeclaredTableSearchIndex<'Table,'Doc>(this.TableName,declared.IndexDescriptor,declared.SearchField,declared.FilterFields)
    member this.BindVectorIndex(table:Table<'Table,'Doc>,index:TableVectorIndex<'Table,'Doc>) =
        SchemaNames.unique this.TableName this.IndexNames
        this.CheckOwner table.Name
        this.CheckOwner index.TableName
        let declared = this.VectorIndexes |> List.tryFind(fun value -> value.IndexDescriptor=index.IndexName)
                       |> Option.defaultWith(fun () -> invalidArg "index" "An active vector index declaration is required")
        if index.VectorField<>declared.VectorField || index.Dimensions<>declared.Dimensions || index.FilterFields<>declared.FilterFields then
            invalidArg "index" "Vector metadata differs from the canonical declaration"
        DeclaredTableVectorIndex<'Table,'Doc>(this.TableName,declared.IndexDescriptor,declared.VectorField,declared.Dimensions,declared.FilterFields)
    member this.WithStagedValidator(validator:Validator) =
        if this.StagedDocumentValidator.IsSome then invalidArg "validator" "Table cannot have more than one staged validator"
        {this with StagedDocumentValidator=Some validator}
    member inline this.WithStagedDocument<'Next>() = this.WithStagedValidator(TypeDerivation.derive<'Next>())

module TableSchema =
    let defineWithValidator (tableName:string) (validator:Validator) : TableDefinition =
        { TableName=tableName;DocumentValidator=validator
          Indexes=[];SearchIndexes=[];VectorIndexes=[]
          StagedIndexes=[];StagedSearchIndexes=[];StagedVectorIndexes=[];StagedDocumentValidator=None }
    let inline define<'Doc> tableName = defineWithValidator tableName (TypeDerivation.derive<'Doc>())
    let inline defineForTable<'Table,'Doc> (table:Table<'Table,'Doc>) = define<'Doc> table.Name
    /// Whole-document validators add system fields recursively to object unions.
    let rec docValidator (tableName:string) (validator:Validator) =
        match validator with
        | Validator.Object fields ->
            fields
            |> Map.add "_id" {FieldType=Validator.Id tableName;Optional=false}
            |> Map.add "_creationTime" {FieldType=Validator.Number;Optional=false}
            |> Validator.Object
        | Validator.Union members -> Validator.Union (List.map (docValidator tableName) members)
        | Validator.Any -> validator
        | _ -> invalidArg "validator" "Table documents must be objects, unions of objects, or an explicit any validator"
    let inline docValidatorForTable<'Table,'Doc> (table:Table<'Table,'Doc>) =
        docValidator table.Name (TypeDerivation.derive<'Doc>())

[<AutoOpen>]
module TableSchemaExtensions =
    type Table<'Table,'Doc> with
        member this.IdValidator = Validator.Id this.Name
        member inline this.DocValidator = TableSchema.docValidatorForTable this

module SchemaJson =
    let private quote = ValidatorJson.quote
    let private strings values = "[" + (values |> List.map quote |> String.concat ",") + "]"
    let private array encode values = "[" + (values |> List.map encode |> String.concat ",") + "]"
    let dbIndex (value:DbIndex) =
        "{\"indexDescriptor\":"+quote value.IndexDescriptor+",\"fields\":"+strings value.Fields+"}"
    let searchIndex (value:SearchIndex) =
        "{\"indexDescriptor\":"+quote value.IndexDescriptor+",\"searchField\":"+quote value.SearchField+",\"filterFields\":"+strings value.FilterFields+"}"
    let vectorIndex (value:VectorIndex) =
        "{\"indexDescriptor\":"+quote value.IndexDescriptor+",\"vectorField\":"+quote value.VectorField+",\"dimensions\":"+string value.Dimensions+",\"filterFields\":"+strings value.FilterFields+"}"
    let table (value:TableDefinition) =
        "{\"tableName\":"+quote value.TableName+
        ",\"indexes\":"+array dbIndex value.Indexes+
        ",\"stagedDbIndexes\":"+array dbIndex value.StagedIndexes+
        ",\"searchIndexes\":"+array searchIndex value.SearchIndexes+
        ",\"stagedSearchIndexes\":"+array searchIndex value.StagedSearchIndexes+
        ",\"vectorIndexes\":"+array vectorIndex value.VectorIndexes+
        ",\"stagedVectorIndexes\":"+array vectorIndex value.StagedVectorIndexes+
        ",\"documentType\":"+value.DocumentValidator.ToJson()+
        (value.StagedDocumentValidator |> Option.map (fun validator -> ",\"stagedDocumentType\":"+validator.ToJson()) |> Option.defaultValue "")+"}"

type SchemaDefinition =
    { Tables:TableDefinition list; SchemaValidation:bool }
    member this.ValidateDeclarations() =
        SchemaNames.unique "schema tables" (this.Tables |> List.map (fun table -> table.TableName))
        for table in this.Tables do SchemaNames.unique table.TableName table.IndexNames
    member private this.Table(name:string) =
        this.Tables |> List.tryFind (fun table -> table.TableName=name)
        |> Option.defaultWith (fun () -> invalidArg "name" ("Table is not declared in this schema: "+name))
    member this.Doc(name:string) =
        let table=this.Table name
        TableSchema.docValidator table.TableName table.DocumentValidator
    member this.Id(name:string) = Validator.Id (this.Table(name).TableName)
    member this.Export() =
        this.ValidateDeclarations()
        "{\"tables\":["+(this.Tables |> List.map SchemaJson.table |> String.concat ",")+
        "],\"schemaValidation\":"+(if this.SchemaValidation then "true" else "false")+"}"

    interface Convex.Runtime.IDeclaration with
        member _.Kind = Convex.Runtime.DeclarationKind.Schema
        member this.Export() = Convex.Runtime.RuntimeJson.parse(this.Export())

module Schema =
    /// Native schema declarations retain the exact owning validators/indexes.
    let toNative (schema:SchemaDefinition) =
        schema.ValidateDeclarations()
        schema
    let defineWithOptions tables schemaValidation =
        let definition={Tables=tables;SchemaValidation=schemaValidation}
        definition.ValidateDeclarations()
        definition
    let define tables = defineWithOptions tables true
