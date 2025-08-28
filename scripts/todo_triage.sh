#!/bin/bash

# TODO Triage Script for OfficialFuturesHedgebotv2
# Analyzes and categorizes actual TODOs and debug statements

echo "=================================================="
echo "      TODO/Debug Triage Script v1.0"
echo "=================================================="
echo ""

# Colors for output
RED='\033[0;31m'
YELLOW='\033[1;33m'
GREEN='\033[0;32m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Create reports directory
mkdir -p reports

# Get current date for report
DATE=$(date +%Y%m%d_%H%M%S)
REPORT_FILE="reports/todo_triage_${DATE}.md"

# Start report
echo "# TODO Triage Report - $(date)" > $REPORT_FILE
echo "" >> $REPORT_FILE

# Function to count occurrences
count_pattern() {
    local pattern=$1
    local exclude=$2
    if [ -z "$exclude" ]; then
        find . -type f \( -name "*.go" -o -name "*.cs" -o -name "*.mq5" -o -name "*.mqh" \) \
            -not -path "*/vcpkg_installed/*" \
            -not -path "*/node_modules/*" \
            -not -path "*/bin/*" \
            -not -path "*/obj/*" \
            -exec grep -h "$pattern" {} \; 2>/dev/null | wc -l
    else
        find . -type f \( -name "*.go" -o -name "*.cs" -o -name "*.mq5" -o -name "*.mqh" \) \
            -not -path "*/vcpkg_installed/*" \
            -not -path "*/node_modules/*" \
            -not -path "*/bin/*" \
            -not -path "*/obj/*" \
            -exec grep -h "$pattern" {} \; 2>/dev/null | grep -v "$exclude" | wc -l
    fi
}

echo -e "${RED}=== CRITICAL: Actual TODO/FIXME Comments ===${NC}"
echo "## Critical TODOs" >> $REPORT_FILE
echo "" >> $REPORT_FILE

# Find real TODOs (excluding DEBUG lines)
TODO_COUNT=$(count_pattern "TODO\|FIXME" "DEBUG")
echo -e "Found ${RED}$TODO_COUNT${NC} real TODO/FIXME comments"
echo "**Count:** $TODO_COUNT" >> $REPORT_FILE
echo "" >> $REPORT_FILE

# List actual TODOs with context
echo -e "\n${YELLOW}Listing actual TODOs:${NC}"
echo "### List of TODOs:" >> $REPORT_FILE
echo '```' >> $REPORT_FILE

find . -type f \( -name "*.go" -o -name "*.cs" -o -name "*.mq5" -o -name "*.mqh" \) \
    -not -path "*/vcpkg_installed/*" \
    -not -path "*/node_modules/*" \
    -not -path "*/bin/*" \
    -not -path "*/obj/*" \
    -exec grep -Hn "TODO\|FIXME" {} \; 2>/dev/null | grep -v "DEBUG" | while read line; do
    echo "$line"
    echo "$line" >> $REPORT_FILE
done

echo '```' >> $REPORT_FILE
echo "" >> $REPORT_FILE

# Debug statements analysis
echo -e "\n${YELLOW}=== DEBUG Statements Analysis ===${NC}"
echo "## Debug Statements" >> $REPORT_FILE
echo "" >> $REPORT_FILE

DEBUG_COUNT=$(count_pattern "DEBUG:" "")
echo -e "Found ${YELLOW}$DEBUG_COUNT${NC} DEBUG statements"
echo "**Count:** $DEBUG_COUNT" >> $REPORT_FILE
echo "" >> $REPORT_FILE

# Break down by file type
echo -e "\n${BLUE}Debug statements by language:${NC}"
echo "### By Language:" >> $REPORT_FILE
echo "" >> $REPORT_FILE

GO_DEBUG=$(find . -name "*.go" -not -path "*/vcpkg_installed/*" -exec grep -h "DEBUG:" {} \; 2>/dev/null | wc -l)
CS_DEBUG=$(find . -name "*.cs" -not -path "*/vcpkg_installed/*" -exec grep -h "DEBUG" {} \; 2>/dev/null | wc -l)
MQ5_DEBUG=$(find . -name "*.mq5" -o -name "*.mqh" -not -path "*/vcpkg_installed/*" -exec grep -h "DEBUG" {} \; 2>/dev/null | wc -l)

echo "  Go files:    $GO_DEBUG"
echo "  C# files:    $CS_DEBUG"
echo "  MQL5 files:  $MQ5_DEBUG"

echo "- Go files: $GO_DEBUG" >> $REPORT_FILE
echo "- C# files: $CS_DEBUG" >> $REPORT_FILE
echo "- MQL5 files: $MQ5_DEBUG" >> $REPORT_FILE
echo "" >> $REPORT_FILE

# WHACK-A-MOLE fixes
echo -e "\n${YELLOW}=== WHACK-A-MOLE Fixes ===${NC}"
echo "## WHACK-A-MOLE Fixes" >> $REPORT_FILE
echo "" >> $REPORT_FILE

WHACK_COUNT=$(count_pattern "WHACK-A-MOLE" "")
echo -e "Found ${YELLOW}$WHACK_COUNT${NC} WHACK-A-MOLE fixes"
echo "**Count:** $WHACK_COUNT" >> $REPORT_FILE
echo "" >> $REPORT_FILE

if [ $WHACK_COUNT -gt 0 ]; then
    echo "### Locations:" >> $REPORT_FILE
    echo '```' >> $REPORT_FILE
    find . -type f \( -name "*.go" -o -name "*.cs" -o -name "*.mq5" -o -name "*.mqh" \) \
        -not -path "*/vcpkg_installed/*" \
        -exec grep -Hn "WHACK-A-MOLE" {} \; 2>/dev/null | while read line; do
        echo "$line" >> $REPORT_FILE
    done
    echo '```' >> $REPORT_FILE
fi

# Generate action items
echo -e "\n${GREEN}=== Generated Action Items ===${NC}"
echo "## Action Items" >> $REPORT_FILE
echo "" >> $REPORT_FILE

echo "### Priority 1: CRITICAL (Do Today)" >> $REPORT_FILE
echo "" >> $REPORT_FILE

if grep -q "MT5-initiated closure" $REPORT_FILE 2>/dev/null; then
    echo "- [ ] Fix MT5-initiated closure handler (MultiStratManager.cs:616)" >> $REPORT_FILE
    echo -e "${RED}1. Fix MT5-initiated closure handler${NC}"
fi

if grep -q "settings retrieval" $REPORT_FILE 2>/dev/null; then
    echo "- [ ] Implement settings retrieval (server.go:408)" >> $REPORT_FILE
    echo -e "${RED}2. Implement settings retrieval${NC}"
fi

echo "" >> $REPORT_FILE
echo "### Priority 2: HIGH (This Week)" >> $REPORT_FILE
echo "" >> $REPORT_FILE

if [ $DEBUG_COUNT -gt 50 ]; then
    echo "- [ ] Remove $DEBUG_COUNT debug statements from production code" >> $REPORT_FILE
    echo -e "${YELLOW}3. Remove $DEBUG_COUNT debug statements${NC}"
fi

if [ $WHACK_COUNT -gt 0 ]; then
    echo "- [ ] Refactor $WHACK_COUNT WHACK-A-MOLE fixes" >> $REPORT_FILE
    echo -e "${YELLOW}4. Refactor WHACK-A-MOLE fixes${NC}"
fi

echo "" >> $REPORT_FILE
echo "### Priority 3: MEDIUM (Next Sprint)" >> $REPORT_FILE
echo "- [ ] Implement proper logging framework" >> $REPORT_FILE
echo "- [ ] Add log level configuration" >> $REPORT_FILE
echo "- [ ] Set up log rotation" >> $REPORT_FILE

# Create cleanup script
echo -e "\n${GREEN}=== Creating Cleanup Scripts ===${NC}"

# Debug cleanup script
cat > scripts/cleanup_debug.sh << 'EOF'
#!/bin/bash
# Removes DEBUG statements from code (backs up first)

echo "Creating backup..."
tar -czf backup_before_debug_cleanup_$(date +%Y%m%d_%H%M%S).tar.gz \
    --exclude='vcpkg_installed' \
    --exclude='node_modules' \
    --exclude='bin' \
    --exclude='obj' \
    *.go *.cs *.mq5 *.mqh

echo "Removing DEBUG statements..."
echo "Preview mode - showing what would be removed:"

# Go files
echo "Go files:"
find . -name "*.go" -not -path "*/vcpkg_installed/*" \
    -exec grep -n "fmt.Println.*DEBUG" {} + | head -5

# To actually remove (uncomment):
# find . -name "*.go" -not -path "*/vcpkg_installed/*" \
#     -exec sed -i '/fmt.Println.*DEBUG/d' {} \;

# C# files
echo "C# files:"
find . -name "*.cs" -not -path "*/vcpkg_installed/*" \
    -exec grep -n "LogToBridge.*DEBUG" {} + | head -5

# To actually remove (uncomment):
# find . -name "*.cs" -not -path "*/vcpkg_installed/*" \
#     -exec sed -i '/LogToBridge.*DEBUG/d' {} \;

# MQL5 files
echo "MQL5 files:"
find . -name "*.mq5" -o -name "*.mqh" -not -path "*/vcpkg_installed/*" \
    -exec grep -n "Print.*DEBUG" {} + | head -5

# To actually remove (uncomment):
# find . -name "*.mq5" -o -name "*.mqh" -not -path "*/vcpkg_installed/*" \
#     -exec sed -i '/Print.*DEBUG/d' {} \;

echo ""
echo "To actually perform cleanup, edit this script and uncomment the sed commands"
EOF

chmod +x scripts/cleanup_debug.sh

# Summary
echo -e "\n${GREEN}=== Summary ===${NC}"
echo "## Summary" >> $REPORT_FILE
echo "" >> $REPORT_FILE

echo -e "Real TODOs:           ${RED}$TODO_COUNT${NC}"
echo -e "Debug Statements:     ${YELLOW}$DEBUG_COUNT${NC}"
echo -e "WHACK-A-MOLE Fixes:   ${YELLOW}$WHACK_COUNT${NC}"

echo "- Real TODOs: **$TODO_COUNT**" >> $REPORT_FILE
echo "- Debug Statements: **$DEBUG_COUNT**" >> $REPORT_FILE
echo "- WHACK-A-MOLE Fixes: **$WHACK_COUNT**" >> $REPORT_FILE
echo "" >> $REPORT_FILE
echo "*Generated: $(date)*" >> $REPORT_FILE

echo -e "\n${GREEN}Report saved to: $REPORT_FILE${NC}"
echo -e "${GREEN}Cleanup script created: scripts/cleanup_debug.sh${NC}"

# Quick fix generator for critical TODOs
cat > scripts/fix_critical_todo.sh << 'EOF'
#!/bin/bash
# Quick fix for the critical MT5-initiated closure TODO

echo "Generating fix for MT5-initiated closure handler..."

cat > /tmp/mt5_closure_fix.cs << 'CSHARP'
// Add this to MultiStratManager.cs at line 616
// Replaces: // TODO: Handle MT5-initiated closure - close corresponding NT position

// Handle MT5-initiated closure - close corresponding NT position
if (action == "MT5_CLOSE_NOTIFICATION" && !string.IsNullOrEmpty(baseId))
{
    LogInfo("GRPC", $"Processing MT5-initiated closure for BaseID: {baseId}");
    
    // Find and close the corresponding NT position
    lock (orderToBaseIdMapLock)
    {
        var ntOrder = orderToBaseIdMap.FirstOrDefault(x => x.Value == baseId).Key;
        if (ntOrder != null)
        {
            LogInfo("GRPC", $"Found NT order {ntOrder.Id} for BaseID {baseId}, initiating closure");
            
            // Submit market order to close the position
            if (ntOrder.Account != null)
            {
                var closeOrder = new Order
                {
                    Account = ntOrder.Account,
                    Instrument = ntOrder.Instrument,
                    OrderAction = ntOrder.OrderAction == OrderAction.Buy ? 
                                  OrderAction.Sell : OrderAction.Buy,
                    OrderType = OrderType.Market,
                    Quantity = ntOrder.Quantity,
                    TimeInForce = TimeInForce.Ioc,
                    Name = $"Close_{baseId}"
                };
                
                ntOrder.Account.Submit(new[] { closeOrder });
                LogInfo("GRPC", $"Submitted close order for NT position {ntOrder.Id}");
            }
        }
        else
        {
            LogWarning("GRPC", $"No NT order found for BaseID {baseId}");
        }
    }
}
CSHARP

echo "Fix generated in /tmp/mt5_closure_fix.cs"
echo "Review and integrate into MultiStratManager.cs at line 616"
EOF

chmod +x scripts/fix_critical_todo.sh

echo -e "\n${GREEN}Critical fix generator created: scripts/fix_critical_todo.sh${NC}"
echo -e "\n${BLUE}Run './scripts/todo_triage.sh' anytime to re-analyze${NC}"