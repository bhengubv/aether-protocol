// SPDX-License-Identifier: MIT

package uri

import (
	"errors"
	"strings"
)

// Builder is a fluent constructor for a URI. Use it when programmatically
// composing an aether:// URI from parts; for parsing an existing string, use
// Parse instead.
//
// Example
//
//	u, err := uri.NewBuilder().
//		Authority("KXJB7-MN2P4").
//		Path("content/sha256-abc123").
//		Query("codec", "opus").
//		Fragment("t=1m30s").
//		Build()
//	// u.String() == "aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus#t=1m30s"
//
// Builder is NOT safe for concurrent use by multiple goroutines.
type Builder struct {
	authority  string
	path       string
	query      map[string]string
	queryOrder []string
	fragment   string
}

// NewBuilder returns an empty Builder.
func NewBuilder() *Builder {
	return &Builder{
		query: map[string]string{},
	}
}

// Authority sets the authority from a raw string (an AetherTag in either
// "XXXXX-XXXXX" or "XXXXXXXXXX" form, or a 64-char hex UHID). The string is
// validated and canonicalised; if validation fails, the bad value is stored
// and Build will return the error.
func (b *Builder) Authority(authority string) *Builder {
	if authority == "" {
		// Defer the error until Build so the fluent chain doesn't panic.
		b.authority = ""
		return b
	}
	// Round-trip through the parser to guarantee validation + canonicalisation.
	if u, err := Parse(schemePrefix + authority); err == nil {
		b.authority = u.Authority
	} else {
		// Store the raw value so Build reports a meaningful error.
		b.authority = "\x00" + authority // sentinel: leading NUL marks "invalid"
	}
	return b
}

// Path sets the path component. Any leading slash is stripped.
func (b *Builder) Path(path string) *Builder {
	b.path = strings.TrimLeft(path, "/")
	return b
}

// AppendPathSegment appends a single segment to the path. Empty segments are
// ignored.
func (b *Builder) AppendPathSegment(segment string) *Builder {
	if segment == "" {
		return b
	}
	trimmed := strings.TrimLeft(segment, "/")
	if b.path == "" {
		b.path = trimmed
	} else {
		b.path = b.path + "/" + trimmed
	}
	return b
}

// Query adds or replaces a query parameter. The key must be non-empty.
// An empty value is permitted and renders as "?key" (flag form).
func (b *Builder) Query(key, value string) *Builder {
	if key == "" {
		// Defer to Build; ignore silently here.
		return b
	}
	lower := strings.ToLower(key)
	if _, exists := b.query[lower]; !exists {
		b.queryOrder = append(b.queryOrder, lower)
	}
	b.query[lower] = value
	return b
}

// RemoveQuery deletes a query parameter by key (case-insensitive). It is a
// no-op if the key is absent.
func (b *Builder) RemoveQuery(key string) *Builder {
	lower := strings.ToLower(key)
	delete(b.query, lower)
	// Leave queryOrder alone; the encoder filters to keys still present.
	return b
}

// Fragment sets the fragment. Any leading '#' is stripped.
func (b *Builder) Fragment(fragment string) *Builder {
	b.fragment = strings.TrimLeft(fragment, "#")
	return b
}

// Build produces a validated URI. Returns an error if the authority is
// missing or if the assembled URI fails to parse.
func (b *Builder) Build() (URI, error) {
	if b.authority == "" {
		return URI{}, errors.New("aether-uri: authority is required")
	}
	if strings.HasPrefix(b.authority, "\x00") {
		return URI{}, &ParseError{
			Input: b.authority[1:],
			Msg:   "invalid authority",
		}
	}
	// Round-trip through Parse so the resulting URI is canonicalised exactly
	// the same way an externally-parsed URI would be.
	return Parse(b.assemble())
}

// assemble renders the current builder state to a raw string (no validation).
// Used by Build to drive a final round-trip through Parse.
func (b *Builder) assemble() string {
	var sb strings.Builder
	sb.Grow(64)
	sb.WriteString(schemePrefix)
	sb.WriteString(b.authority)
	if b.path != "" {
		sb.WriteByte('/')
		encodePath(&sb, b.path)
	}
	if len(b.query) > 0 {
		sb.WriteByte('?')
		first := true
		for _, k := range b.queryKeyOrder() {
			if !first {
				sb.WriteByte('&')
			}
			first = false
			encodeComponent(&sb, k, encodeQueryKey)
			v := b.query[k]
			if v != "" {
				sb.WriteByte('=')
				encodeComponent(&sb, v, encodeQueryValue)
			}
		}
	}
	if b.fragment != "" {
		sb.WriteByte('#')
		encodeComponent(&sb, b.fragment, encodeFragment)
	}
	return sb.String()
}

// queryKeyOrder returns the builder's query keys in insertion order, filtered
// to those still present in the map.
func (b *Builder) queryKeyOrder() []string {
	out := make([]string, 0, len(b.query))
	seen := make(map[string]struct{}, len(b.query))
	for _, k := range b.queryOrder {
		if _, ok := b.query[k]; !ok {
			continue
		}
		if _, dup := seen[k]; dup {
			continue
		}
		seen[k] = struct{}{}
		out = append(out, k)
	}
	if len(out) != len(b.query) {
		for k := range b.query {
			if _, ok := seen[k]; !ok {
				seen[k] = struct{}{}
				out = append(out, k)
			}
		}
	}
	return out
}
