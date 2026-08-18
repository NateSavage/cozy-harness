{
  description = "Agent harness — an always-on agent with persistent memory and self-authored goals";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
  };

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system:
        f { inherit system; pkgs = nixpkgs.legacyPackages.${system}; });
    in
    {
      packages = forAllSystems ({ pkgs, ... }: rec {
        agent-harness = pkgs.callPackage ./nix/package.nix { };
        default = agent-harness;
      });

      devShells = forAllSystems ({ pkgs, ... }: {
        default = pkgs.mkShell {
          packages = with pkgs; [
            dotnet-sdk_10
            sqlite            # for poking at index.sqlite by hand
            git
            jq
            curl              # for hitting llama-server /health and /completion
            llama-cpp
          ];

          # Keeps dotnet from phoning home and from writing outside the tree.
          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
          DOTNET_NOLOGO = "1";
          DOTNET_ROOT = "${pkgs.dotnet-sdk_10}/share/dotnet";

          shellHook = ''
            echo "agent-harness dev shell"
            echo "  dotnet run -- agent.json          # run against a local tree"
            echo "  dotnet run -- --rebuild-only      # regenerate the index"
            echo "  nix build .#agent-harness.passthru.fetch-deps && ./result nix/deps.json"
          '';
        };
      });

      nixosModules = {
        agent-harness = import ./nix/module.nix;
        default = self.nixosModules.agent-harness;
      };

      overlays.default = final: prev: {
        agent-harness = final.callPackage ./nix/package.nix { };
      };
    };
}
