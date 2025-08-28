//go:build tools_smoke

package main

import "fmt"

// Minimal smoke test binary guarded by build tag so it doesn't affect default builds
func main() {
	fmt.Println("tools/smoke: ok")
}
