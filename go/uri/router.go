// SPDX-License-Identifier: MIT

package uri

import (
	"context"
	"errors"
	"fmt"
	"sync"
)

// DispatchContext is delivered to a registered URI handler when its route
// matches. It carries the original URI plus any captured route parameters.
type DispatchContext struct {
	// URI is the original URI being dispatched.
	URI URI

	// Handler is the descriptor that matched.
	Handler HandlerDescriptor

	// RouteParameters holds the captured route parameters from the match.
	// Keys correspond to the template placeholder names; empty when the
	// matched descriptor has no placeholders.
	RouteParameters map[string]string
}

// HandlerFunc is the callback signature registered with a Router. Returning
// an error causes Dispatch to propagate it to the caller; the error is NOT
// wrapped.
type HandlerFunc func(ctx context.Context, dc *DispatchContext) error

// Router dispatches an incoming aether:// URI to the registered handler for
// its route. Each app constructs one with its own manifest.
//
// Lifecycle
//
//  1. App startup: build a HandlerManifest describing every route the app
//     accepts.
//  2. App startup: register a callback per HandlerDescriptor via Register.
//  3. At runtime: when a URI is received, call Dispatch (or DispatchString)
//     to invoke the matching callback.
//
// Router is safe for concurrent use by multiple goroutines — Register and
// Dispatch may be called from any goroutine.
type Router struct {
	manifest *HandlerManifest

	mu       sync.Mutex
	handlers map[handlerKey]HandlerFunc
}

// handlerKey identifies a descriptor for registration purposes. We key by
// (Name, PathTemplate) so two same-Name descriptors with different templates
// (e.g. /profile and /profile/avatar) are registered independently.
type handlerKey struct {
	name         string
	pathTemplate string
}

// keyFor returns the registration key for d.
func keyFor(d HandlerDescriptor) handlerKey {
	return handlerKey{name: d.Name, pathTemplate: d.PathTemplate}
}

// NewRouter constructs a Router bound to the given manifest. Returns an error
// if the manifest is nil.
func NewRouter(m *HandlerManifest) (*Router, error) {
	if m == nil {
		return nil, errors.New("aether-uri: manifest is nil")
	}
	return &Router{
		manifest: m,
		handlers: map[handlerKey]HandlerFunc{},
	}, nil
}

// Manifest returns the manifest the router resolves against.
func (r *Router) Manifest() *HandlerManifest {
	return r.manifest
}

// Register attaches a callback to a descriptor. The descriptor MUST be one
// present in the router's manifest (compared by Name + PathTemplate).
// Re-registering replaces the existing callback.
func (r *Router) Register(d HandlerDescriptor, handler HandlerFunc) error {
	if handler == nil {
		return errors.New("aether-uri: handler is nil")
	}
	if !r.manifestContains(d) {
		return fmt.Errorf("aether-uri: descriptor %q is not in the manifest", d.Name)
	}
	r.mu.Lock()
	r.handlers[keyFor(d)] = handler
	r.mu.Unlock()
	return nil
}

// manifestContains reports whether d is in the bound manifest.
func (r *Router) manifestContains(d HandlerDescriptor) bool {
	for _, h := range r.manifest.Handlers {
		if h.Name == d.Name && h.PathTemplate == d.PathTemplate {
			return true
		}
	}
	return false
}

// Dispatch resolves uri against the manifest and invokes the registered
// callback. Returns (true, nil) on a successful dispatch, (false, nil) when
// no handler matched or no callback was registered for the matched
// descriptor, and (false, err) when the callback itself returned an error.
// The callback's error is propagated unwrapped.
func (r *Router) Dispatch(ctx context.Context, u URI) (bool, error) {
	resolved, ok := r.manifest.Resolve(u)
	if !ok {
		return false, nil
	}
	r.mu.Lock()
	cb, hasCb := r.handlers[keyFor(resolved.Handler)]
	r.mu.Unlock()
	if !hasCb {
		return false, nil
	}
	dc := &DispatchContext{
		URI:             u,
		Handler:         resolved.Handler,
		RouteParameters: resolved.Captures,
	}
	if err := cb(ctx, dc); err != nil {
		return false, err
	}
	return true, nil
}

// DispatchString parses s and dispatches the result. Returns the parse error
// directly if parsing fails; otherwise behaves like Dispatch.
func (r *Router) DispatchString(ctx context.Context, s string) (bool, error) {
	u, err := Parse(s)
	if err != nil {
		return false, err
	}
	return r.Dispatch(ctx, u)
}
