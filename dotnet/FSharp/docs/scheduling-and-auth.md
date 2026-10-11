# Native scheduling and identity

Mutation scheduling participates in the owning transaction; action scheduling is separately owned. Scheduler targets retain mutation/action kind and public/internal visibility through canonical references or durable handles. Queries do not receive scheduling authority. Use actual contracts in [Scheduler.fs](../src/Convex.FSharp/Scheduler.fs), [FunctionReferences.fs](../src/Convex.FSharp/FunctionReferences.fs) and [Context.fs](../src/Convex.FSharp/Context.fs).

Cron declarations are native values exported by the declaration owner. Retain durable cron names, intervals, target addresses and argument codecs. Physical file moves do not choose another deployed identity.

Auth identity is supplied and verified by the original backend owner. A projected identity claim is not a membership grant. Preserve issuer/audience/token identity, enabled-principal and current authorization checks at application effects. Queries and mutations use owner-supplied clock/randomness rather than ambient .NET time, task identity or scheduling.

Use safe Convex application errors with deliberate message/data. Historical role or activation receipts establish their original outcome; subsequent effects revalidate current authority. Actual scheduler, cancellation, cron and JWT behavior requires current provider fixtures.
