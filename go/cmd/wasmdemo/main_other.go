// SPDX-License-Identifier: MIT
//go:build !js || !wasm

// Non-browser stub so this package still builds under a normal `go build ./...` / `go test ./...`.
// The real demo lives in main.go behind the `js && wasm` build tag.
package main

import "fmt"

func main() {
	fmt.Println("wasmdemo is a browser (js/wasm) target; build with: GOOS=js GOARCH=wasm go build -o aether.wasm ./cmd/wasmdemo")
}
