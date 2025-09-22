package main

import (
	"fmt"
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
)

const (
	iconSize        = 64
	defaultIconPath = "BridgeApp/build/appicon.png"
	dirPerm         = os.FileMode(0o755)
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintf(os.Stderr, "genicon: %v\n", err)
		os.Exit(1)
	}
}

func run() (err error) {
	outputPath := filepath.FromSlash(defaultIconPath)
	if len(os.Args) > 1 {
		outputPath = os.Args[1]
	}

	if err := ensureOutputDir(outputPath); err != nil {
		return fmt.Errorf("failed to prepare output directory: %w", err)
	}

	img := image.NewRGBA(image.Rect(0, 0, iconSize, iconSize))
	for y := 0; y < iconSize; y++ {
		for x := 0; x < iconSize; x++ {
			img.Set(x, y, color.RGBA{R: 30, G: 60, B: 90, A: 255})
		}
	}

	f, err := os.Create(outputPath)
	if err != nil {
		return fmt.Errorf("failed to create icon file: %w", err)
	}

	defer func() {
		if cerr := f.Close(); cerr != nil && err == nil {
			err = fmt.Errorf("failed to close icon file: %w", cerr)
		}
	}()

	if err := png.Encode(f, img); err != nil {
		return fmt.Errorf("failed to encode icon: %w", err)
	}

	return nil
}

func ensureOutputDir(outputPath string) error {
	dir := filepath.Dir(outputPath)
	if dir == "" || dir == "." {
		return nil
	}
	return os.MkdirAll(dir, dirPerm)
}
