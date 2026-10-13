# What remains

Current behavior and exercised checks are in [CAPABILITIES](../CAPABILITIES.md),
[release scope](../RELEASE_SCOPE.md), and
[implementation status](../IMPLEMENTATION_STATUS.md). The package version in
`Directory.Build.props` is 0.3.1.

The installed 0.3.1 distribution reuses the optimized backend built from source
`606f0395`. This checkout includes later backend commits, so it is not that
installed engine.

These are not claimed:

- Official Convex support or managed Convex Cloud admission.
- A public package registry or a public source release.
- Every operating system, CPU, or container combination.
- Arbitrary hostile CLR assemblies or every third-party NuGet package.
- Qualification of the inherited Docker recipe.
- Long-duration production behavior.

`artifacts/` is generated output. `dotnet/tools/PackageExamples.fsx` recreates
example capsules after the example projects build. `dotnet/tools/Release.fsx`
creates packages and a runtime bundle when invoked. Neither command reproduces
the recorded 0.3.1 bytes from this newer tree.
