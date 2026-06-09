// SPDX-License-Identifier: MIT

package uri

import (
	"errors"
	"strings"
)

// HandlerDescriptor describes a single handler an app exposes on its
// aether:// URI surface.
//
// A handler is identified by its first path segment (the Name) plus an
// optional path template that captures route parameters. The router matches
// an incoming URI's HandlerName + path against a manifest of these and
// dispatches accordingly.
//
// Path template syntax
//
//	"content/{hash}"             // matches /content/abc
//	"watch/{sessionId}/join"     // matches /watch/123/join
//	"profile"                    // matches /profile exactly
//	"profile/avatar"             // matches /profile/avatar exactly
type HandlerDescriptor struct {
	// Name is the handler name — the first path segment (e.g. "content",
	// "stream").
	Name string

	// PathTemplate is the route template applied AFTER the handler name
	// (e.g. "{hash}", "{sessionId}/join"). Empty for a root handler that
	// matches exactly the handler name with no extra segments.
	PathTemplate string

	// ExpectedQueryKeys lists query keys the handler expects (informational
	// — not enforced by the manifest).
	ExpectedQueryKeys []string

	// Description is a human-readable description for diagnostics + docs.
	Description string
}

// Validate reports an error if the descriptor is missing a handler name.
func (d HandlerDescriptor) Validate() error {
	if strings.TrimSpace(d.Name) == "" {
		return errors.New("aether-uri: handler name is required")
	}
	return nil
}

// Match attempts to match the descriptor's template against an incoming URI
// path (the path component of a URI — already percent-decoded). Returns the
// captured route parameters and true on success, or (nil, false) on no match.
func (d HandlerDescriptor) Match(path string) (map[string]string, bool) {
	var templateSegs []string
	if d.PathTemplate == "" {
		templateSegs = []string{d.Name}
	} else {
		templateSegs = strings.Split(d.Name+"/"+strings.TrimLeft(d.PathTemplate, "/"), "/")
	}
	pathSegs := strings.Split(path, "/")
	if len(templateSegs) != len(pathSegs) {
		return nil, false
	}

	captures := map[string]string{}
	for i, t := range templateSegs {
		p := pathSegs[i]
		if len(t) >= 2 && t[0] == '{' && t[len(t)-1] == '}' {
			captures[t[1:len(t)-1]] = p
			continue
		}
		if t != p {
			return nil, false
		}
	}
	return captures, true
}

// ResolvedRoute is the result of resolving a URI against a HandlerManifest —
// the matched descriptor plus any captured route parameters.
type ResolvedRoute struct {
	// Handler is the descriptor that matched.
	Handler HandlerDescriptor

	// Captures holds the captured route parameters, keyed by template
	// placeholder name. Empty for handlers with no placeholders.
	Captures map[string]string
}

// HandlerManifest is an app's complete aether:// handler manifest — the set
// of routes the app accepts. Each app registers exactly one manifest at
// startup; the router dispatches against it.
type HandlerManifest struct {
	// AppID is the owning app's identifier (e.g. "aether.media",
	// "aether.txtme"). Reverse-DNS style.
	AppID string

	// Handlers is the ordered list of registered handler descriptors.
	// Order is significant for Resolve — the first matching descriptor wins.
	Handlers []HandlerDescriptor
}

// NewHandlerManifest constructs a HandlerManifest. Returns an error if the
// AppID is empty or any descriptor in handlers is invalid.
func NewHandlerManifest(appID string, handlers []HandlerDescriptor) (*HandlerManifest, error) {
	if strings.TrimSpace(appID) == "" {
		return nil, errors.New("aether-uri: appID is required")
	}
	for i, h := range handlers {
		if err := h.Validate(); err != nil {
			return nil, errFromIndex(i, err)
		}
	}
	// Copy to avoid aliasing.
	cp := make([]HandlerDescriptor, len(handlers))
	copy(cp, handlers)
	return &HandlerManifest{AppID: appID, Handlers: cp}, nil
}

// errFromIndex prefixes err with the offending handler index.
func errFromIndex(i int, err error) error {
	if err == nil {
		return nil
	}
	return errors.New("aether-uri: handler at index " + itoa(i) + ": " + err.Error())
}

// itoa is a tiny dependency-free int-to-string helper for small indices.
func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var buf [20]byte
	i := len(buf)
	for n > 0 {
		i--
		buf[i] = byte('0' + n%10)
		n /= 10
	}
	if neg {
		i--
		buf[i] = '-'
	}
	return string(buf[i:])
}

// Resolve walks the manifest's handlers in order and returns the first one
// whose template matches uri's path, together with its captured parameters.
// Returns (nil, false) if no handler matches or if uri is invalid.
func (m *HandlerManifest) Resolve(u URI) (*ResolvedRoute, bool) {
	if m == nil || !u.IsValid() {
		return nil, false
	}
	handlerName := u.HandlerName()
	for _, h := range m.Handlers {
		if h.Name != handlerName {
			continue
		}
		captures, ok := h.Match(u.Path)
		if !ok {
			continue
		}
		return &ResolvedRoute{Handler: h, Captures: captures}, true
	}
	return nil, false
}
