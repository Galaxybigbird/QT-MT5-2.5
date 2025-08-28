#!/bin/bash
# Quick MultiStratManager deployment script

#!/bin/bash
echo "🚀 Deploying MultiStratManager files..."

NT_SOURCE="./MultiStratManagerRepo"
NT_TARGET="/mnt/c/Users/marth/OneDrive/Desktop/OneDrive/Old video editing files/NinjaTrader 8/bin/Custom/AddOns/MultiStratManager"

if [[ ! -d "$NT_TARGET" ]]; then
    echo "❌ NinjaTrader target not found: $NT_TARGET"
    exit 1
fi

echo "📦 Source: $NT_SOURCE"
echo "📦 Target: $NT_TARGET"
echo ""

# Deploy critical files
files=("MultiStratManager.cs" "UIForManager.cs" "SLTPRemovalLogic.cs" "TrailingAndElasticManager.cs" "app.config")

count=0
for file in "${files[@]}"; do
    if [[ -f "$NT_SOURCE/$file" ]]; then
        echo "  📄 Deploying $file..."
        cp "$NT_SOURCE/$file" "$NT_TARGET/$file"
        echo "  ✅ $file deployed"
        count=$((count + 1))
    else
        echo "  ⚠️ $file not found"
    fi
done

# Deploy External directory
if [[ -d "$NT_SOURCE/External" ]]; then
    echo ""
    echo "📦 Deploying External dependencies..."
    mkdir -p "$NT_TARGET/External"
    mkdir -p "$NT_TARGET/External/Proto"
    
    # Copy critical external files
    if [[ -f "$NT_SOURCE/External/NTGrpcClient.dll" ]]; then
        cp "$NT_SOURCE/External/NTGrpcClient.dll" "$NT_TARGET/External/"
        echo "  ✅ NTGrpcClient.dll deployed"
    fi
    
    if [[ -f "$NT_SOURCE/External/Proto/Trading.cs" ]]; then
        cp "$NT_SOURCE/External/Proto/Trading.cs" "$NT_TARGET/External/Proto/"
        echo "  ✅ Proto/Trading.cs deployed"
    fi
    
    # Copy all gRPC dependencies
    for dll in "$NT_SOURCE/External/"*.dll; do
        if [[ -f "$dll" ]]; then
            cp "$dll" "$NT_TARGET/External/"
        fi
    done
    echo "  ✅ All gRPC dependencies deployed"
fi

echo ""
echo "🎉 Deployment completed! ($count core files deployed)"
echo ""
echo "📋 Next Steps:"
echo "1. Open NinjaTrader 8"
echo "2. Go to Tools → Edit NinjaScript → AddOn"
echo "3. Open MultiStratManager"
echo "4. Press F5 to compile"
echo "5. Test MT5 closure handler with small trade"
echo ""
echo "🔧 Critical Fix Location:"
echo "MultiStratManager.cs line 616 - HandleMT5InitiatedClosure"