#!/bin/bash

# Multi-Repository Migration Script
# OfficialFuturesHedgebotv2 Trading System

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
PROJECT_ROOT=$(pwd)
GITHUB_ORG=${GITHUB_ORG:-"your-trading-org"}  # Set your GitHub org
SWARM_DIR="$PROJECT_ROOT/.swarm"
TEMP_DIR="$PROJECT_ROOT/.temp-migration"

echo -e "${BLUE}🔄 Multi-Repository Migration for Trading System${NC}"
echo "=================================================="

# Check prerequisites
check_prerequisites() {
    echo -e "${YELLOW}📋 Checking prerequisites...${NC}"
    
    # Check if gh CLI is installed
    if ! command -v gh &> /dev/null; then
        echo -e "${RED}❌ GitHub CLI (gh) is required but not installed${NC}"
        echo "Install from: https://cli.github.com/"
        exit 1
    fi
    
    # Check if authenticated
    if ! gh auth status &> /dev/null; then
        echo -e "${RED}❌ Not authenticated with GitHub CLI${NC}"
        echo "Run: gh auth login"
        exit 1
    fi
    
    # Check if git is clean
    if [[ -n $(git status --porcelain) ]]; then
        echo -e "${YELLOW}⚠️  Working directory has uncommitted changes${NC}"
        echo "Please commit or stash changes before migration"
        exit 1
    fi
    
    echo -e "${GREEN}✅ Prerequisites checked${NC}"
}

# Create GitHub repositories
create_github_repos() {
    echo -e "${YELLOW}🏗️  Creating GitHub repositories...${NC}"
    
    declare -a repos=(
        "trading-protocol:Shared protobuf definitions and contracts"
        "trading-shared-lib:Common utilities and helper functions"
        "trading-bridge-core:Central communication hub and API gateway"
        "trading-bridge-ui:Web-based trading dashboard and controls"
        "ninjatrader-addon:NinjaTrader addon and strategy management"
        "mt5-expert-advisor:MT5 Expert Advisor and hedge management"
    )
    
    for repo_info in "${repos[@]}"; do
        IFS=':' read -r repo_name repo_desc <<< "$repo_info"
        
        echo -e "Creating repository: ${BLUE}$repo_name${NC}"
        
        # Check if repo already exists
        if gh repo view "$GITHUB_ORG/$repo_name" &> /dev/null; then
            echo -e "${YELLOW}⚠️  Repository $repo_name already exists, skipping${NC}"
        else
            gh repo create "$GITHUB_ORG/$repo_name" \
                --private \
                --description "$repo_desc" \
                --add-readme \
                --gitignore="Node" \
                --license="MIT"
            
            echo -e "${GREEN}✅ Created $repo_name${NC}"
        fi
    done
}

# Phase 1: Extract Protocol Repository
extract_protocol_repo() {
    echo -e "${YELLOW}📦 Phase 1: Extracting Protocol Repository...${NC}"
    
    local protocol_repo="$GITHUB_ORG/trading-protocol"
    local temp_dir="$TEMP_DIR/trading-protocol"
    
    # Create temporary directory
    mkdir -p "$temp_dir"
    
    # Initialize git repo
    cd "$temp_dir"
    git init
    git remote add origin "https://github.com/$protocol_repo.git"
    
    # Copy protocol files
    echo -e "📋 Copying protocol files..."
    mkdir -p proto
    
    # Copy from BridgeApp
    if [[ -d "$PROJECT_ROOT/BridgeApp/proto" ]]; then
        cp -r "$PROJECT_ROOT/BridgeApp/proto/"* proto/
    fi
    
    # Copy from MT5
    if [[ -f "$PROJECT_ROOT/MT5/proto/trading.proto" ]]; then
        cp "$PROJECT_ROOT/MT5/proto/trading.proto" proto/
    fi
    
    # Copy from MultiStratManager
    if [[ -d "$PROJECT_ROOT/MultiStratManagerRepo/External/Proto" ]]; then
        cp -r "$PROJECT_ROOT/MultiStratManagerRepo/External/Proto/"* proto/
    fi
    
    # Create README
    cat > README.md << 'EOF'
# Trading Protocol

Shared protobuf definitions and gRPC contracts for the trading system.

## Components

- `trading.proto` - Core trading message definitions
- `streaming.proto` - Real-time streaming service definitions

## Usage

### Go
```bash
protoc --go_out=. --go-grpc_out=. proto/*.proto
```

### C#
```bash
protoc --csharp_out=./Generated proto/*.proto
```

### C++ (for MT5)
```bash
protoc --cpp_out=./generated proto/*.proto
```

## Version Compatibility

| Protocol Version | Bridge Core | NinjaTrader Addon | MT5 EA |
|-----------------|-------------|-------------------|---------|
| 1.0.0           | ^1.0.0      | ^1.0.0           | ^1.0.0  |

## Breaking Changes

See [CHANGELOG.md](CHANGELOG.md) for breaking changes and migration guides.
EOF
    
    # Create package.json for versioning
    cat > package.json << EOF
{
  "name": "@trading-system/protocol",
  "version": "1.0.0",
  "description": "Shared protobuf definitions for trading system",
  "main": "index.js",
  "scripts": {
    "generate-go": "protoc --go_out=./generated/go --go-grpc_out=./generated/go proto/*.proto",
    "generate-csharp": "protoc --csharp_out=./generated/csharp proto/*.proto",
    "generate-cpp": "protoc --cpp_out=./generated/cpp proto/*.proto",
    "generate-all": "npm run generate-go && npm run generate-csharp && npm run generate-cpp"
  },
  "keywords": ["trading", "protobuf", "grpc"],
  "license": "MIT"
}
EOF
    
    # Create generation directories
    mkdir -p generated/{go,csharp,cpp}
    
    # Commit and push
    git add .
    git commit -m "Initial commit: Extract protocol definitions

- Consolidated proto files from all components
- Added generation scripts for Go, C#, and C++
- Established versioning strategy"
    
    git branch -M main
    git push -u origin main
    
    echo -e "${GREEN}✅ Protocol repository created and pushed${NC}"
    cd "$PROJECT_ROOT"
}

# Phase 2: Extract Shared Library
extract_shared_lib_repo() {
    echo -e "${YELLOW}📚 Phase 2: Extracting Shared Library Repository...${NC}"
    
    local shared_repo="$GITHUB_ORG/trading-shared-lib"
    local temp_dir="$TEMP_DIR/trading-shared-lib"
    
    mkdir -p "$temp_dir"
    cd "$temp_dir"
    
    git init
    git remote add origin "https://github.com/$shared_repo.git"
    
    # Create multi-language structure
    mkdir -p {go,csharp,cpp}/{src,tests}
    
    # Create Go module
    cat > go/go.mod << 'EOF'
module github.com/trading-system/shared-lib/go

go 1.21

require (
    google.golang.org/grpc v1.58.0
    google.golang.org/protobuf v1.31.0
)
EOF
    
    # Create C# project
    cat > csharp/TradingSharedLib.csproj << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>8.0</LangVersion>
  </PropertyGroup>
  
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.24.0" />
    <PackageReference Include="Grpc.Net.Client" Version="2.57.0" />
  </ItemGroup>
</Project>
EOF
    
    # Create shared utilities (placeholder)
    cat > go/src/trade_utils.go << 'EOF'
package shared

import (
    "time"
)

// TradeCalculator provides common trading calculations
type TradeCalculator struct{}

// CalculateProfitLoss calculates P&L for a trade
func (tc *TradeCalculator) CalculateProfitLoss(entryPrice, exitPrice, quantity float64, isLong bool) float64 {
    if isLong {
        return (exitPrice - entryPrice) * quantity
    }
    return (entryPrice - exitPrice) * quantity
}

// FormatTradeID generates a consistent trade ID format
func FormatTradeID(timestamp time.Time, symbol string, sequence int) string {
    return fmt.Sprintf("TRADE_%s_%s_%04d", 
        timestamp.Format("20060102_150405"), 
        symbol, 
        sequence)
}
EOF
    
    # Create README
    cat > README.md << 'EOF'
# Trading Shared Library

Common utilities, models, and helper functions used across all trading system components.

## Languages Supported

- **Go**: Core utilities for Bridge application
- **C#**: Utilities for NinjaTrader addon
- **C++**: Utilities for MT5 Expert Advisor

## Structure

```
├── go/         # Go utilities
├── csharp/     # C# utilities  
├── cpp/        # C++ utilities
└── docs/       # Documentation
```

## Usage

### Go
```bash
go mod tidy
import "github.com/trading-system/shared-lib/go"
```

### C#
```bash
dotnet add package TradingSystem.SharedLib
```

### C++
```bash
# Include in your CMakeLists.txt
find_package(TradingSharedLib REQUIRED)
```

## Contributing

1. Add functionality to appropriate language directory
2. Include tests
3. Update documentation
4. Version bump according to semantic versioning
EOF
    
    git add .
    git commit -m "Initial commit: Shared library structure

- Multi-language support (Go, C#, C++)
- Common trading utilities
- Consistent project structure"
    
    git branch -M main
    git push -u origin main
    
    echo -e "${GREEN}✅ Shared library repository created${NC}"
    cd "$PROJECT_ROOT"
}

# Update current repository with submodules
setup_submodules() {
    echo -e "${YELLOW}🔗 Setting up submodules in current repository...${NC}"
    
    # Add protocol as submodule
    git submodule add "https://github.com/$GITHUB_ORG/trading-protocol.git" .submodules/protocol
    
    # Add shared lib as submodule
    git submodule add "https://github.com/$GITHUB_ORG/trading-shared-lib.git" .submodules/shared-lib
    
    # Initialize submodules
    git submodule update --init --recursive
    
    # Create symlinks for backwards compatibility
    echo -e "${YELLOW}📎 Creating compatibility symlinks...${NC}"
    
    # Link protocol files
    if [[ -d "BridgeApp/proto" ]]; then
        rm -rf BridgeApp/proto
        ln -sf "../.submodules/protocol/proto" BridgeApp/proto
    fi
    
    if [[ -d "MT5/proto" ]]; then
        rm -rf MT5/proto
        ln -sf "../.submodules/protocol/proto" MT5/proto
    fi
    
    # Commit submodule setup
    git add .
    git commit -m "Setup: Add protocol and shared-lib as submodules

- Added trading-protocol repository as submodule
- Added trading-shared-lib repository as submodule  
- Created compatibility symlinks for existing code
- Enables coordinated development across repositories"
    
    echo -e "${GREEN}✅ Submodules configured${NC}"
}

# Create GitHub Actions for coordination
setup_github_actions() {
    echo -e "${YELLOW}⚙️  Setting up GitHub Actions for coordination...${NC}"
    
    mkdir -p .github/workflows
    
    # Multi-repo sync workflow
    cat > .github/workflows/multi-repo-sync.yml << 'EOF'
name: Multi-Repository Synchronization

on:
  repository_dispatch:
    types: [protocol-updated, shared-lib-updated]
  workflow_dispatch:
    inputs:
      sync_type:
        description: 'Type of sync to perform'
        required: true
        default: 'all'
        type: choice
        options:
        - all
        - protocol
        - shared-lib

jobs:
  sync-submodules:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive
          token: ${{ secrets.GITHUB_TOKEN }}
          
      - name: Update Submodules
        run: |
          git submodule update --remote --merge
          
      - name: Check for Changes
        id: changes
        run: |
          if [[ -n $(git status --porcelain) ]]; then
            echo "changes=true" >> $GITHUB_OUTPUT
          else
            echo "changes=false" >> $GITHUB_OUTPUT
          fi
          
      - name: Create Pull Request
        if: steps.changes.outputs.changes == 'true'
        uses: peter-evans/create-pull-request@v5
        with:
          token: ${{ secrets.GITHUB_TOKEN }}
          commit-message: "chore: Update submodules to latest versions"
          title: "Update Protocol and Shared Library Dependencies"
          body: |
            This PR updates the submodules to their latest versions.
            
            ## Changes
            - Updated trading-protocol submodule
            - Updated trading-shared-lib submodule
            
            ## Testing
            - [ ] Bridge application builds successfully
            - [ ] NinjaTrader addon compiles
            - [ ] MT5 EA compiles
            - [ ] Integration tests pass
          branch: update-submodules
          
  notify-completion:
    needs: sync-submodules
    runs-on: ubuntu-latest
    if: always()
    steps:
      - name: Notify Swarm Coordinator
        run: |
          echo "Multi-repo sync completed"
          # Add webhook notification here if needed
EOF
    
    # Integration test workflow
    cat > .github/workflows/integration-tests.yml << 'EOF'
name: Cross-Component Integration Tests

on:
  pull_request:
    paths:
      - '.submodules/**'
      - 'BridgeApp/**'
      - 'MultiStratManagerRepo/**'
      - 'MT5/**'
  workflow_dispatch:

jobs:
  integration-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive
          
      - name: Setup Go
        uses: actions/setup-go@v4
        with:
          go-version: '1.21'
          
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '6.0'
          
      - name: Build Bridge Application
        run: |
          cd BridgeApp
          go mod tidy
          go build -o bridge-test
          
      - name: Build NinjaTrader Addon
        run: |
          cd MultiStratManagerRepo
          dotnet build
          
      - name: Run Integration Tests
        run: |
          echo "Running cross-component integration tests..."
          # Add actual integration test commands here
          
      - name: Generate Test Report
        if: always()
        run: |
          echo "Integration test results would be reported here"
EOF
    
    git add .github/
    git commit -m "ci: Add multi-repository coordination workflows

- Added submodule synchronization workflow
- Added cross-component integration testing
- Enables automated coordination across repositories"
    
    echo -e "${GREEN}✅ GitHub Actions configured${NC}"
}

# Create swarm coordination script
create_swarm_coordination() {
    echo -e "${YELLOW}🤖 Creating swarm coordination scripts...${NC}"
    
    cat > scripts/swarm-coordinate.sh << 'EOF'
#!/bin/bash

# Swarm Coordination Script for Multi-Repository Operations

set -e

SWARM_CONFIG=".swarm/multi-repo-config.yml"
GITHUB_ORG=${GITHUB_ORG:-"your-trading-org"}

# Initialize swarm for multi-repo operation
init_swarm() {
    echo "🚀 Initializing multi-repository swarm..."
    
    # Use ruv-swarm to initialize coordination
    npx ruv-swarm@latest swarm-init \
        --topology hierarchical \
        --max-agents 8 \
        --strategy adaptive \
        --enable-coordination \
        --enable-memory
}

# Coordinate dependency updates across repos
coordinate_dependency_update() {
    local dependency=$1
    local version=$2
    
    echo "📦 Coordinating dependency update: $dependency@$version"
    
    # Protocol repository
    echo "Updating protocol repository..."
    gh repo clone "$GITHUB_ORG/trading-protocol" /tmp/protocol
    cd /tmp/protocol
    
    # Update dependency (example for Node.js)
    if [[ -f package.json ]]; then
        npm install "$dependency@$version"
        git add package.json package-lock.json
        git commit -m "chore: Update $dependency to $version"
        git push origin main
    fi
    
    # Trigger updates in dependent repositories
    gh api repos/$GITHUB_ORG/trading-bridge-core/dispatches \
        --field event_type=protocol-updated \
        --field client_payload='{"dependency":"'$dependency'","version":"'$version'"}'
        
    cd - && rm -rf /tmp/protocol
}

# Run cross-repository tests
run_cross_repo_tests() {
    echo "🧪 Running cross-repository integration tests..."
    
    # Trigger integration tests across all repositories
    local repos=("trading-bridge-core" "trading-bridge-ui" "ninjatrader-addon" "mt5-expert-advisor")
    
    for repo in "${repos[@]}"; do
        echo "Triggering tests in $repo..."
        gh workflow run integration-tests.yml \
            --repo "$GITHUB_ORG/$repo"
    done
}

# Monitor swarm health across repositories
monitor_swarm_health() {
    echo "💓 Monitoring swarm health across repositories..."
    
    # Check each repository's status
    local repos=("trading-protocol" "trading-shared-lib" "trading-bridge-core" "trading-bridge-ui" "ninjatrader-addon" "mt5-expert-advisor")
    
    for repo in "${repos[@]}"; do
        echo "Checking $repo..."
        
        # Get latest workflow runs
        gh run list --repo "$GITHUB_ORG/$repo" --limit 5 --json status,conclusion,name
    done
}

# Main command dispatcher
case "${1:-}" in
    "init")
        init_swarm
        ;;
    "update-dependency")
        coordinate_dependency_update "$2" "$3"
        ;;
    "test")
        run_cross_repo_tests
        ;;
    "health")
        monitor_swarm_health
        ;;
    *)
        echo "Usage: $0 {init|update-dependency|test|health}"
        echo ""
        echo "Commands:"
        echo "  init                          Initialize multi-repo swarm"
        echo "  update-dependency <dep> <ver> Coordinate dependency update"
        echo "  test                          Run cross-repo integration tests"
        echo "  health                        Monitor swarm health"
        exit 1
        ;;
esac
EOF
    
    chmod +x scripts/swarm-coordinate.sh
    
    echo -e "${GREEN}✅ Swarm coordination scripts created${NC}"
}

# Main execution flow
main() {
    echo -e "${BLUE}Starting multi-repository migration...${NC}"
    
    # Create temp directory
    mkdir -p "$TEMP_DIR"
    
    case "${1:-all}" in
        "check")
            check_prerequisites
            ;;
        "repos")
            check_prerequisites
            create_github_repos
            ;;
        "protocol")
            check_prerequisites
            extract_protocol_repo
            ;;
        "shared")
            check_prerequisites
            extract_shared_lib_repo
            ;;
        "submodules")
            setup_submodules
            ;;
        "actions")
            setup_github_actions
            ;;
        "coordination")
            create_swarm_coordination
            ;;
        "all")
            check_prerequisites
            create_github_repos
            extract_protocol_repo
            extract_shared_lib_repo
            setup_submodules
            setup_github_actions
            create_swarm_coordination
            ;;
        *)
            echo "Usage: $0 {check|repos|protocol|shared|submodules|actions|coordination|all}"
            echo ""
            echo "Phases:"
            echo "  check        Check prerequisites only"
            echo "  repos        Create GitHub repositories"
            echo "  protocol     Extract protocol repository"
            echo "  shared       Extract shared library repository"
            echo "  submodules   Setup submodules in current repo"
            echo "  actions      Setup GitHub Actions"
            echo "  coordination Create swarm coordination scripts"
            echo "  all          Run all phases (default)"
            exit 1
            ;;
    esac
    
    # Cleanup
    if [[ -d "$TEMP_DIR" ]]; then
        rm -rf "$TEMP_DIR"
    fi
    
    echo -e "${GREEN}🎉 Multi-repository migration completed successfully!${NC}"
    echo ""
    echo "Next steps:"
    echo "1. Set GITHUB_ORG environment variable to your GitHub organization"
    echo "2. Run: ./scripts/multi-repo-migration.sh check"
    echo "3. Run: ./scripts/multi-repo-migration.sh all"
    echo "4. Update CI/CD pipelines to use new repository structure"
    echo "5. Train team on multi-repository workflow"
}

# Run main function with all arguments
main "$@"