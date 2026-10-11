# Distribution and publication boundary

This is a community-maintained Convex native F#/.NET preview, not an official Convex runtime or a Convex Cloud feature.

The complete backend/fork retains the root FSL-1.1-Apache-2.0 license, including its permitted-purpose and notice requirements. The F# frontend retains its separately recorded Apache-2.0 metadata. Do not relabel the complete fork as Apache, erase notices, or imply upstream endorsement. Review commercial distribution and hosting plans against the actual licenses with qualified advice.

Only publish deliberately built packages, runtime artifacts and curated source. Do not change repository visibility as a side effect of package work. Review Git history before publishing it. Credentials, customer data, journals, .local, bin/obj, private fixtures and audit conversations are not public-source inputs.

Local publication means writing to the selected NuGet feed. It is not a push to NuGet.org or a GitHub release. Use a registry namespace and package identifiers the project owner controls before a public release. The release producer refuses replacing an existing version with different package bytes; increment Version when contents change. The application/tool/template dependency versions must match the selected release.

Minimum release check: package-only starter build and code generation; handler-free client compilation; SDK/executor tests; disposable full-backend transaction, reactivity, auth and cancellation checks; reproducible backend provenance; supported runtime/container platforms. Known limitations must remain visible. Do not claim production readiness or full Convex parity based on package compilation alone.
