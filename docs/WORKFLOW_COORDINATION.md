# Centralized Development Workflow - Hive Mind Coordination

## 🎯 CRITICAL WORKFLOW CONTEXT FOR HIVE MIND SWARM

### Master Development Environment
- **Primary Repo**: `C:\Documents\Dev\OfficialFuturesHedgebotv2` (THIS CURRENT DIRECTORY)
- **Role**: Central development hub where ALL code changes are made
- **Strategy**: Edit here first, then propagate to deployment locations

### Deployment Target Mapping

#### 1. MetaTrader 5 (MT5) HedgeBot
- **Source**: `./MT5/` directory in this repo
- **Target**: `C:\Users\marth\AppData\Roaming\MetaQuotes\Terminal\7BC3F33EDFDBDBDBADB45838B9A2D03F\MQL5`
- **Key Files**: 
  - `ACHedgeMaster_gRPC.mq5`
  - Include files (`ACFunctions.mqh`, `ATRtrailing.mqh`, `StatusOverlay.mqh`)
  - Generated gRPC files
- **Process**: Edit in this repo → Copy to MT5 terminal → Manual compile in MetaEditor

#### 2. NinjaTrader 8 MultiStratManager
- **Source**: `./MultiStratManagerRepo/` directory in this repo  
- **Target**: `C:\Users\marth\OneDrive\Desktop\OneDrive\Old video editing files\NinjaTrader 8\bin\Custom\AddOns\MultiStratManager`
- **Process**: Edit in this repo → Copy to NinjaTrader AddOns → Manual compile in NinjaTrader

### Agent Responsibilities

#### 🤖 File Synchronization Agents
- **Monitor**: Changes in master repo directories
- **Execute**: Automated copying to deployment locations
- **Verify**: File integrity and successful transfers
- **Report**: Copy operations and any conflicts

#### 🔧 Development Agents  
- **Focus**: Make ALL code changes in this master repo
- **Never**: Edit files directly in deployment locations
- **Always**: Test changes here before propagation

### Workflow Rules
1. ✅ **EDIT HERE FIRST**: All code modifications happen in this repo
2. ✅ **PROPAGATE OUTWARD**: Use agents to copy to deployment locations
3. ✅ **MANUAL COMPILATION**: User handles compilation in respective IDEs
4. ✅ **CENTRALIZED TESTING**: Test and validate in this environment
5. ❌ **NO DIRECT EDITS**: Never modify files in deployment locations directly

### Communication Protocol
- **Status Updates**: Report all copy operations
- **Conflict Resolution**: Alert on file conflicts or copy failures  
- **Change Tracking**: Log all modifications and their propagation
- **Coordination**: Ensure agents work together on multi-file changes

### Current Active Session
- **Swarm ID**: `swarm-1754521169137-t3sy8mabk`
- **Session ID**: `session-1754521169292-4tdl32ps7`
- **Objective**: Analyze full system with centralized workflow awareness