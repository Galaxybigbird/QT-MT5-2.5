#\!/bin/bash
# Quick MultiStratManager deployment script

set -e
echo "🚀 Deploying MultiStratManager files..."

NT_SOURCE="./MultiStratManagerRepo"
NT_TARGET="/mnt/c/Users/marth/OneDrive/Desktop/OneDrive/Old video editing files/NinjaTrader 8/bin/Custom/AddOns/MultiStratManager"

if [[ \! -d "$NT_TARGET" ]]; then
    echo "❌ NinjaTrader target not found: $NT_TARGET"
    exit 1
fi

echo "📦 Source: $NT_SOURCE"
echo "📦 Target: $NT_TARGET"

# Deploy critical files
files=("MultiStratManager.cs" "UIForManager.cs" "SLTPRemovalLogic.cs" "TrailingAndElasticManager.cs" "app.config")

for file in "${files[@]}"; do
    if [[ -f "$NT_SOURCE/$file" ]]; then
        echo "  Deploying $file..."
        cp "$NT_SOURCE/$file" "$NT_TARGET/$file"
        echo "  ✅ $file deployed"
    else
        echo "  ⚠️ $file not found"
    fi
done

# Deploy External directory
if [[ -d "$NT_SOURCE/External" ]]; then
    echo "📦 Deploying External dependencies..."
    mkdir -p "$NT_TARGET/External"
    cp -r "$NT_SOURCE/External/"* "$NT_TARGET/External/" 2>/dev/null || true
    echo "✅ External dependencies deployed"
fi

echo "🎉 Deployment completed\!"
echo ""
echo "Next Steps:"
echo "1. Open NinjaTrader 8"
echo "2. Go to Tools → Edit NinjaScript → AddOn"
echo "3. Open MultiStratManager"
echo "4. Press F5 to compile"
echo "5. Test MT5 closure handler"
EOF < /dev/null
