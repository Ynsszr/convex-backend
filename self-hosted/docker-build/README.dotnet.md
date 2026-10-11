# Native .NET image extension

1. Build the existing `Dockerfile.backend` from this fork using upstream's build mechanics.
2. Build `Dockerfile.dotnet` with `CONVEX_BACKEND_IMAGE` selecting that built fork image, ideally by digest.
3. The image adds CoreCLR, the managed host/developer CLI and bubblewrap. It preserves the inherited entrypoint and data volume.
4. Prepare a native runtime selection for the container's actual immutable paths, mount it read-only, and explicitly set `CONVEX_DOTNET_MANIFEST` for the backend. Do not reuse host filesystem paths inside containers.

The Docker recipe is an image extension, NOT a complete preconfigured deployment. The separate runtime distribution producer and `convex-dotnet runtime prepare`/`runtime run` provide native local deployment artifacts. Container selection/launch needs host-specific user-namespace and seccomp/AppArmor support; native workers fail closed. Do not use privileged mode or remove the native sandbox to hide a configuration failure.

Inherited Compose recipes still select upstream registry images until an operator explicitly replaces them. Never publish to upstream-owned registries. Retain upstream FSL notices. Container build/run has not been verified by the developer-experience package checks.
