# 11. These libraries are referenced, not published

Date: 2026-08-05
Status: Accepted

Supersedes the packaging parts of the task specification, which assumed a NuGet package.

## Context

The repository was built to the task specification, which asks for a shipping NuGet package:
package metadata, symbols, Source Link, an `IsPackable` project, and public API tracked in
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` so that a released surface is a promise.

The repository owner has since decided these libraries are consumed directly — a project reference
or an assembly reference — and are not published to a feed. That removes the audience every one of
those mechanisms was for.

## Decision

Remove the publishing apparatus, and say so once rather than leaving the pieces lying around.

Gone:

- All `Package*` metadata, `IncludeSymbols`, `SymbolPackageFormat`, Source Link and the
  `RepositoryUrl`/`RepositoryType` pair. Source Link exists so that a consumer of a *binary* can step
  into source it does not have; a project reference is built from the source already.
- Packing README and LICENSE into a package, and the `dotnet pack` step and package upload in CI.
- `Microsoft.CodeAnalysis.PublicApiAnalyzers`, both `PublicAPI.*.txt` files in each project, the
  `RS0016`/`RS0017` severities, and `PublicApiTests`.
- `PackagingTests`, which checked metadata that no longer exists.

Kept, because none of it was about publishing:

- `GenerateDocumentationFile`. A referencing project reads the XML file for IntelliSense, so the
  documentation is for whoever writes against these libraries. An architecture test also reads it to
  check every diagnostic code is documented.
- `Deterministic` and `EmbedUntrackedSources`: reproducibility is worth having regardless.
- `Description`, which becomes assembly metadata.
- Every architecture guard about boundaries, the public surface, and the diagnostic catalogue. Those
  enforce the design, not the distribution.

`IsPackable=false` is now set once at the repository root. The SDK default for a library is packable,
so saying nothing would have left `dotnet pack` quietly producing packages nobody meant to exist.

## Consequences

- **The public surface is no longer tracked in a file.** That was the one genuine loss, and it is
  worth naming: the API files were a reviewable diff of what a change added to the surface, which no
  test now provides. The trade is deliberate. Their purpose was a compatibility promise to consumers
  who upgrade a package independently, and consumers that are rebuilt alongside the library do not
  need one — while the cost was real, since every change required a convergence pass to regenerate
  them.
- Breaking a public signature is now caught by the compiler in the consuming project rather than by
  a diff in this one. For a library rebuilt with its consumers, that is the same information sooner.
- Thirty-two tests went with the two deleted files. Nothing they covered is now unguarded except the
  API-file bookkeeping itself.
- If publishing is ever wanted, this ADR is the list of what to put back, and it should be
  superseded rather than quietly reversed.
