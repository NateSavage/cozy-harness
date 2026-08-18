{ lib
, buildDotnetModule
, dotnetCorePackages
, git
, sqlite
, makeWrapper
}:

buildDotnetModule rec {
  pname = "cozy-harness";
  version = "0.1.0";

  src = lib.cleanSourceWith {
    src = ../.;
    filter = path: type:
      let base = baseNameOf path; in
      !(builtins.elem base [ "bin" "obj" "result" ".git" ]);
  };

  projectFile = "CozyHarness.csproj";

  # Generate with:
  #   nix build .#cozy-harness.passthru.fetch-deps
  #   ./result nix/deps.json
  # fetch-deps always emits JSON; addNuGetDeps only decides whether to
  # importJSON or callPackage based on the extension you give it here, so
  # the filename must stay .json (not .nix) on current nixpkgs.
  nugetDeps = ./deps.json;

  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  dotnet-runtime = dotnetCorePackages.runtime_10_0;

  nativeBuildInputs = [ makeWrapper ];

  executables = [ "CozyHarness" ];

  # Full transparency: the agent can read its own source and design whenever it
  # wants. The module symlinks this into its tree as `harness/`. Available
  # always, loaded never — force-feeding self-reference crowds out the world.
  postInstall = ''
    mkdir -p $out/share/cozy-harness
    cp -r --no-preserve=mode,ownership $src/. $out/share/cozy-harness/
    rm -rf $out/share/cozy-harness/nix/deps.json
  '';

  # GitStore shells out to `git` rather than linking a library, deliberately:
  # the agent shares this environment and should be able to run the same
  # commands itself. So git must be on PATH at runtime, not just at build time.
  postFixup = ''
    wrapProgram $out/bin/CozyHarness \
      --prefix PATH : ${lib.makeBinPath [ git sqlite ]} \
      --set DOTNET_CLI_TELEMETRY_OPTOUT 1 \
      --set DOTNET_NOLOGO 1
  '';

  meta = with lib; {
    description = "Always-on agent harness with persistent memory and self-authored goals";
    platforms = platforms.linux;
    mainProgram = "CozyHarness";
  };
}
