# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Project Overview

UAssetStudio is a comprehensive .NET 9.0 toolchain for analyzing, decompiling, compiling, and verifying Unreal Engine game assets (.uasset files). The project specializes in Blueprint (Kismet) visual scripting analysis and supports UE versions 4.13-5.6+.

## Core Capabilities

1. **Control Flow Graph (CFG) Generation** - Creates `.dot` files and summaries from UE assets
2. **Decompilation** - Converts `.uasset/.umap` files to human-readable `.kms` (Kismet Script) format
3. **Compilation** - Converts `.kms` scripts back to compiled `.uasset/.uexp` files
4. **Verification** - Validates complete round-trip: decompile → compile → link → write
5. **Standalone Compilation** - Compile `.kms` with metadata (no original asset required)

## Solution Architecture

```
UAssetStudio.sln
├── UAssetStudio.Cli/                    # Main CLI entry point (.NET 9.0)
├── UAssetAPI/                           # Core asset reading/writing library (git submodule)
│   ├── UAssetAPI/                       # Main library
│   ├── UAssetAPI.Tests/                 # Unit tests
│   └── UAssetAPI.Benchmark/             # Performance tests
├── KismetScript.Compiler/               # ANTLR-based Kismet script compiler
├── KismetScript.Decompiler/             # Asset decompilation logic
├── KismetScript.Linker/                 # Asset linking and compilation
├── KismetScript.Parser/                 # Kismet script parser
├── KismetScript.Syntax/                 # AST and syntax tree definitions
├── KismetScript.Utilities/              # Shared utility classes
├── KismetAnalyzer.CFG/                  # Control flow graph generation
├── AssetRegistry.Serializer/            # Asset registry serialization
└── UAsset.Localization/                 # Localization support
```

## Essential Commands

### Build Commands
```bash
# Build entire solution
dotnet build

# Build specific configuration
dotnet build --configuration Release
dotnet build --configuration DebugTracing  # With PostSharp tracing

# Run all tests
dotnet test
dotnet test --verbosity normal

# Run specific test project
dotnet test UAssetAPI/UAssetAPI.Tests/UAssetAPI.Tests.csproj

# Run single test by name
dotnet test --filter "FullyQualifiedName~TestMethodName"
```

### CLI Usage
```bash
# Global options:
#   --ue-version <ver>    Engine version (VER_UE4_27, VER_UE5_6, etc.)
#   --mappings <path>     .usmap file (required for UE5+, optional for UE4)

# Generate control flow graph
dotnet run --project UAssetStudio.Cli -- cfg <asset> --mappings <usmap> --ue-version VER_UE5_6 --outdir <dir>

# Decompile asset to .kms script (add --meta to generate metadata for standalone compilation)
dotnet run --project UAssetStudio.Cli -- decompile <asset> --mappings <usmap> --ue-version VER_UE5_6 --outdir <dir> [--meta]

# Compile .kms script back to asset
dotnet run --project UAssetStudio.Cli -- compile <script.kms> --asset <original.uasset> --mappings <usmap> --ue-version VER_UE5_6 --outdir <dir>

# Verify complete round-trip (add --meta to test standalone compilation path)
dotnet run --project UAssetStudio.Cli -- verify <asset> --mappings <usmap> --ue-version VER_UE5_6 --outdir <dir> [--meta]
```

### Test Scripts
```bash
# Run verification tests
./script/test_verify_5.6.sh   # UE5.6 test (requires .usmap)
./script/test_verify_4.27.sh  # UE4.27 test (no .usmap required)

# Other test scripts
./script/test_compile.sh
./script/test_decompile.sh
./script/test_gen_dot.sh
```

## Key Development Patterns

### Binary Equality Requirement
This project maintains **binary equality** with original assets - critical for game modding. Always verify with `VerifyBinaryEquality()` method when modifying serialization.

### Property Type System
- Uses reflection-based automatic discovery (`MainSerializer.cs:78-134`)
- Property types extend `PropertyData` and are auto-registered
- Located in `UAssetAPI/PropertyTypes/` with Objects/ and Structs/ subdirectories

### Engine Version Support
- Primary: UE4.27, UE5.6
- Version specified via `--ue-version` flag (e.g., `VER_UE4_27`, `VER_UE5_6`)
- Test assets organized by version in `script/test_case_*/`

### Asset Processing Flow
1. **Reading**: `UAsset.cs` → `MainSerializer.cs` → Property type handlers
2. **Decompilation**: Asset → Kismet bytecode → `.kms` script
3. **Compilation**: `.kms` script → Kismet bytecode → Linked asset
4. **Verification**: Original → Decompile → Compile → Compare

## Critical Files and Entry Points

### Core Library (UAssetAPI)
- `UAssetAPI/UAssetAPI/UAsset.cs` - Main asset loading/saving API
- `UAssetAPI/UAssetAPI/MainSerializer.cs` - Serialization engine and property registry (property discovery at lines 78-134)
- `UAssetAPI/UAssetAPI/AssetBinaryReader.cs` / `AssetBinaryWriter.cs` - Binary I/O
- `UAssetAPI/UAssetAPI/ExportTypes/Export.cs` - Base export class (12 export types total)

### CLI Tool
- `UAssetStudio.Cli/Program.cs` - Command parsing and orchestration
- `UAssetStudio.Cli/CMD/VerifyCommand.cs` - Verification workflow
- `UAssetStudio.Cli/CMD/CompileCommand.cs` - Compilation logic
- `UAssetStudio.Cli/CMD/DecompileCommand.cs` - Decompilation logic

### Compiler/Decompiler
- `KismetScript.Decompiler/KismetDecompiler.cs` - Main decompiler entry point
- `KismetScript.Decompiler/MetadataExtractor.cs` - Extracts metadata for standalone compilation
- `KismetScript.Compiler/` - ANTLR-based parser for `.kms` files
- `KismetScript.Linker/UAssetLinker.cs` - Asset linking and final compilation
- `KismetScript.Syntax/` - Abstract syntax tree and syntax definitions

## Testing Infrastructure

### Test Asset Organization
- `script/test_case_4_27/` - UE4.27 specific tests
- `script/test_case_5_6/` - UE5.6 specific tests with real game assets
- `UAssetAPI/UAssetAPI.Tests/` - Comprehensive unit test suite

### Real Game Assets Include
- `BP_PlayerControllerBase.uasset` - Blueprint controller
- `WPN_ZipLineGun.uasset` - Weapon system asset
- Assets from F1Manager2023, Palworld, Tekken, etc.

### Test Categories
- JSON serialization tests
- Multi-engine version compatibility
- Game-specific asset handling
- Custom property type testing

## Important Considerations

### Mappings Files
- `.usmap` files required for unversioned property parsing in UE5+
- UE4 versions (e.g., VER_UE4_27) can often work without mappings
- Specified via `--mappings` parameter

### Standalone Compilation
- Use `--meta` flag during decompilation to generate `.kms.meta` JSON file
- Metadata file captures asset context needed to compile without original asset
- Enables distribution of `.kms` + `.kms.meta` for modding workflows

### Custom Serialization Flags
Available in `UAssetAPI/UAssetAPI/UAsset.cs`:
- `NoDummies` - Skip dummy property generation
- `SkipParsingBytecode` - Skip Kismet bytecode parsing
- `SkipPreloadDependencyLoading` - Skip preload dependencies
- `SkipParsingExports` - Skip export parsing

### Error Handling
- Use `Assert` class for validation with detailed diff output
- Always maintain binary compatibility for game modding use cases
