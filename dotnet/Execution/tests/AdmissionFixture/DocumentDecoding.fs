namespace RuntimeFixture

open Convex.FSharp

type AdmissionDocument = { name: string }
type AdmissionDocumentWithMetadata = { name: string; _id: string; _creationTime: float }

/// Exercise the public database extensions from an optimized application assembly.
type NativeDocumentFixtures private () =
    static member read =
        Convex.query (fun (context: QueryCtx) () ->
            context.Db.Query<AdmissionDocument, AdmissionDocument>("admissionDocuments").Unique())

    static member readMetadata =
        Convex.query (fun (context: QueryCtx) () ->
            context.Db.Query<AdmissionDocumentWithMetadata, AdmissionDocumentWithMetadata>("admissionDocuments").Unique())
