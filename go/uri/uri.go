// SPDX-License-Identifier: MIT

// Package uri provides the Aether URI scheme — the canonical addressing format
// for resources on the Aether mesh.
//
// Grammar (ABNF, RFC 5234)
//
//	aether-uri   = "aether://" authority [ "/" path ] [ "?" query ] [ "#" fragment ]
//	authority    = aether-tag / uhid
//	aether-tag   = 5(crockford) [ "-" ] 5(crockford)         ; case-insensitive
//	uhid         = 64(HEXDIG)                                ; SHA-256 hex of public key
//	path         = path-segment *( "/" path-segment )
//	path-segment = 1*( unreserved / pct-encoded / sub-delims / ":" / "@" )
//	query        = query-param *( "&" query-param )
//	query-param  = key [ "=" value ]
//	key          = 1*( unreserved / pct-encoded )
//	value        = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
//	fragment     = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
//
// Components
//
//   - Scheme is always "aether". Case-insensitive on parse, lowercase on emit.
//   - Authority identifies the destination — an AetherTag (10 Crockford
//     base-32 chars, dash optional) or a UHID (64 hex chars). Case-insensitive.
//   - Path is opaque to the protocol — it names a handler within the
//     destination (e.g. /content/<hash>, /profile, /inbox). Case-sensitive.
//   - Query carries handler arguments. Keys are case-insensitive, values are
//     case-sensitive.
//   - Fragment is a client-side hint and is never transmitted over the wire.
//
// Why no userinfo or port
//
// The authority IS the user — there is no separate userinfo. Ports have no
// meaning in mesh routing because the transport layer selects the carrier
// (BLE / Wi-Fi Direct / NearLink / HTTP relay); a URI never picks one.
package uri

import (
	"errors"
	"fmt"
	"strings"

	"github.com/bhengubv/aether-protocol/go/identity"
)

// Scheme is the fixed scheme name — "aether".
const Scheme = "aether"

// schemePrefix is the literal scheme + "://" prefix.
const schemePrefix = "aether://"

// ParseError is returned by Parse when the input is not a valid aether URI.
type ParseError struct {
	// Input is the original string that failed to parse.
	Input string
	// Msg is a human-readable description of what went wrong.
	Msg string
}

// Error implements the error interface.
func (e *ParseError) Error() string {
	if e.Input == "" {
		return "aether-uri: " + e.Msg
	}
	return "aether-uri: " + e.Msg + " (input=" + e.Input + ")"
}

// URI is a parsed aether:// URI. The zero value is not valid; use Parse or a
// Builder to construct one.
type URI struct {
	// Authority is the destination — an AetherTag in canonical "XXXXX-XXXXX"
	// form or an upper-case 64-char hex UHID.
	Authority string

	// Path is the handler path, without the leading slash. Empty string means
	// "root" (no path).
	Path string

	// Query holds decoded query parameters. Keys are stored lower-case for
	// case-insensitive lookup; values are case-sensitive. A flag-style
	// parameter ("?flag") is represented as an entry with an empty value.
	Query map[string]string

	// Fragment is the fragment, with the leading "#" stripped. Empty if none.
	Fragment string

	// queryOrder preserves the insertion order of query keys so the canonical
	// encoder emits them in the same order as the source. Unexported because
	// it is an implementation detail of the canonical-form encoder; it does
	// NOT participate in Equal.
	queryOrder []string
}

// IsValid reports whether u was produced by a successful parse (i.e. has a
// non-empty authority).
func (u URI) IsValid() bool {
	return u.Authority != ""
}

// HandlerName returns the first path segment — the handler name — or the
// empty string for the root path.
func (u URI) HandlerName() string {
	if u.Path == "" {
		return ""
	}
	if slash := strings.IndexByte(u.Path, '/'); slash >= 0 {
		return u.Path[:slash]
	}
	return u.Path
}

// PathSegments returns the path split into segments after percent-decoding
// each one. Returns an empty slice for the root path.
func (u URI) PathSegments() []string {
	if u.Path == "" {
		return []string{}
	}
	return strings.Split(u.Path, "/")
}

// Equal reports whether two URIs have the same authority, path, fragment, and
// query map (key case-insensitive, order-insensitive).
func (u URI) Equal(other URI) bool {
	if u.Authority != other.Authority {
		return false
	}
	if u.Path != other.Path {
		return false
	}
	if u.Fragment != other.Fragment {
		return false
	}
	if len(u.Query) != len(other.Query) {
		return false
	}
	// Keys are already stored lower-case so direct comparison works.
	for k, v := range u.Query {
		ov, ok := other.Query[k]
		if !ok || ov != v {
			return false
		}
	}
	return true
}

// String returns the canonical string form of the URI. Two URIs that compare
// Equal produce the same canonical string except that query parameter order
// is not part of equality but IS part of the rendered form (insertion order
// is preserved by the Builder; Parse uses the order they appeared in the
// source). The encoder applies RFC 3986 percent-encoding using the per-kind
// allow-lists in IsAllowedUnencoded.
func (u URI) String() string {
	if !u.IsValid() {
		return ""
	}
	var sb strings.Builder
	sb.Grow(64)
	sb.WriteString(schemePrefix)
	sb.WriteString(u.Authority)
	if u.Path != "" {
		sb.WriteByte('/')
		encodePath(&sb, u.Path)
	}
	if len(u.Query) > 0 {
		sb.WriteByte('?')
		first := true
		for _, k := range u.queryKeyOrder() {
			if !first {
				sb.WriteByte('&')
			}
			first = false
			encodeComponent(&sb, k, encodeQueryKey)
			v := u.Query[k]
			if v != "" {
				sb.WriteByte('=')
				encodeComponent(&sb, v, encodeQueryValue)
			}
		}
	}
	if u.Fragment != "" {
		sb.WriteByte('#')
		encodeComponent(&sb, u.Fragment, encodeFragment)
	}
	return sb.String()
}

// queryKeyOrder returns the query keys in insertion order if a slice of
// preserved keys is attached, otherwise sorted alphabetically for
// determinism. The Parse / Builder code uses preservedKeys via a per-URI
// hidden slice; here we just produce a stable order by iterating the map.
//
// For the corpus the rendered order matches the input order because the C#
// reference iterates its Dictionary insertion order. Go's map iteration is
// randomised, so we keep insertion order explicitly via the orderedQueryKeys
// field on the URI (populated by Parse / Builder).
func (u URI) queryKeyOrder() []string {
	if u.queryOrder != nil {
		// Filter to only keys still present (RemoveQuery in builder may have
		// trimmed entries from Query but left them in queryOrder).
		out := make([]string, 0, len(u.Query))
		seen := make(map[string]struct{}, len(u.Query))
		for _, k := range u.queryOrder {
			if _, ok := u.Query[k]; ok {
				if _, dup := seen[k]; dup {
					continue
				}
				seen[k] = struct{}{}
				out = append(out, k)
			}
		}
		// Append any keys that were never registered in the order list (e.g.
		// map-only construction).
		if len(out) != len(u.Query) {
			for k := range u.Query {
				if _, ok := seen[k]; !ok {
					seen[k] = struct{}{}
					out = append(out, k)
				}
			}
		}
		return out
	}
	keys := make([]string, 0, len(u.Query))
	for k := range u.Query {
		keys = append(keys, k)
	}
	// Sort lexicographically — stable across runs.
	for i := 1; i < len(keys); i++ {
		for j := i; j > 0 && keys[j-1] > keys[j]; j-- {
			keys[j-1], keys[j] = keys[j], keys[j-1]
		}
	}
	return keys
}

// Parse parses an aether:// URI. Returns a *ParseError on any syntactic
// violation.
func Parse(input string) (URI, error) {
	if input == "" {
		return URI{}, &ParseError{Input: input, Msg: "input is empty"}
	}

	// Scheme is case-insensitive per RFC 3986.
	if len(input) < len(schemePrefix) ||
		!strings.EqualFold(input[:len(schemePrefix)], schemePrefix) {
		return URI{}, &ParseError{Input: input, Msg: "scheme must be 'aether://'"}
	}

	rest := input[len(schemePrefix):]

	// Split on fragment first (only one '#' is allowed).
	var fragment string
	if fragSplit := strings.IndexByte(rest, '#'); fragSplit >= 0 {
		fragRaw := rest[fragSplit+1:]
		decoded, err := percentDecode(fragRaw)
		if err != nil {
			return URI{}, &ParseError{Input: input, Msg: "malformed fragment: " + err.Error()}
		}
		fragment = decoded
		rest = rest[:fragSplit]
	}

	// Then query.
	query := map[string]string{}
	var queryOrder []string
	if querySplit := strings.IndexByte(rest, '?'); querySplit >= 0 {
		queryRaw := rest[querySplit+1:]
		rest = rest[:querySplit]
		for _, pair := range strings.Split(queryRaw, "&") {
			if pair == "" {
				continue
			}
			var key, value string
			if eq := strings.IndexByte(pair, '='); eq >= 0 {
				key = pair[:eq]
				value = pair[eq+1:]
			} else {
				key = pair
			}
			decodedKey, err := percentDecode(key)
			if err != nil {
				return URI{}, &ParseError{Input: input, Msg: "malformed query key: " + err.Error()}
			}
			if decodedKey == "" {
				return URI{}, &ParseError{Input: input, Msg: "empty query parameter key"}
			}
			decodedValue, err := percentDecode(value)
			if err != nil {
				return URI{}, &ParseError{Input: input, Msg: "malformed query value: " + err.Error()}
			}
			// Match C# behaviour: keys are stored lower-case for case-insensitive
			// lookup. Later occurrences of the same key overwrite earlier ones.
			lower := strings.ToLower(decodedKey)
			if _, exists := query[lower]; !exists {
				queryOrder = append(queryOrder, lower)
			}
			query[lower] = decodedValue
		}
	}

	// Then path.
	var authorityRaw, pathRaw string
	if pathSplit := strings.IndexByte(rest, '/'); pathSplit >= 0 {
		authorityRaw = rest[:pathSplit]
		pathRaw = rest[pathSplit+1:]
	} else {
		authorityRaw = rest
	}

	if authorityRaw == "" {
		return URI{}, &ParseError{Input: input, Msg: "authority is missing"}
	}

	authority, err := canonicaliseAuthority(authorityRaw)
	if err != nil {
		return URI{}, &ParseError{Input: input, Msg: err.Error()}
	}

	if err := validatePath(pathRaw); err != nil {
		return URI{}, &ParseError{Input: input, Msg: err.Error()}
	}

	decodedPath, err := percentDecodePath(pathRaw)
	if err != nil {
		return URI{}, &ParseError{Input: input, Msg: "malformed path: " + err.Error()}
	}

	return URI{
		Authority:  authority,
		Path:       decodedPath,
		Query:      query,
		Fragment:   fragment,
		queryOrder: queryOrder,
	}, nil
}

// canonicaliseAuthority validates and canonicalises an authority. It accepts
// either a 64-char hex UHID (rendered upper-case) or an AetherTag (rendered
// in canonical "XXXXX-XXXXX" form via the identity package).
func canonicaliseAuthority(raw string) (string, error) {
	if len(raw) == 64 && isHex(raw) {
		return strings.ToUpper(raw), nil
	}
	if tag, ok := identity.TryParse(raw); ok {
		return tag.Value, nil
	}
	return "", fmt.Errorf("authority %q is neither a valid AetherTag nor a 64-char hex UHID", raw)
}

// isHex reports whether s consists entirely of hex digits.
func isHex(s string) bool {
	for i := 0; i < len(s); i++ {
		if !isHexChar(s[i]) {
			return false
		}
	}
	return true
}

// validatePath walks the path segments and rejects empty segments, illegal
// characters, and malformed percent-encoding.
func validatePath(path string) error {
	if path == "" {
		return nil
	}
	for _, segment := range strings.Split(path, "/") {
		if segment == "" {
			return errors.New("empty path segment (consecutive slashes)")
		}
		for i := 0; i < len(segment); i++ {
			c := segment[i]
			if isUnreserved(c) || isSubDelim(c) || c == ':' || c == '@' {
				continue
			}
			if c == '%' {
				if i+2 >= len(segment) || !isHexChar(segment[i+1]) || !isHexChar(segment[i+2]) {
					return fmt.Errorf("malformed percent-encoding at position %d of segment %q", i, segment)
				}
				i += 2
				continue
			}
			return fmt.Errorf("illegal character %q in path segment %q", c, segment)
		}
	}
	return nil
}

// ── character classes (RFC 3986) ─────────────────────────────────────────────

func isUnreserved(c byte) bool {
	return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
		c == '-' || c == '.' || c == '_' || c == '~'
}

func isSubDelim(c byte) bool {
	switch c {
	case '!', '$', '&', '\'', '(', ')', '*', '+', ',', ';', '=':
		return true
	}
	return false
}

func isHexChar(c byte) bool {
	return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')
}

func hexValue(c byte) int {
	switch {
	case c <= '9':
		return int(c - '0')
	case c <= 'F':
		return int(c-'A') + 10
	default:
		return int(c-'a') + 10
	}
}

// ── percent-encoding ─────────────────────────────────────────────────────────

// encodeKind selects the per-component allow-list used by encodeComponent.
type encodeKind int

const (
	encodePathSegment encodeKind = iota
	encodeQueryKey
	encodeQueryValue
	encodeFragment
)

// encodePath writes a slash-separated path, encoding each segment under the
// path-segment allow-list.
func encodePath(sb *strings.Builder, path string) {
	first := true
	for _, segment := range strings.Split(path, "/") {
		if !first {
			sb.WriteByte('/')
		}
		first = false
		encodeComponent(sb, segment, encodePathSegment)
	}
}

// encodeComponent percent-encodes value under the given kind's allow-list.
// Non-allowed code points are UTF-8 encoded and each byte is emitted as %XX.
func encodeComponent(sb *strings.Builder, value string, kind encodeKind) {
	// Iterate runes so multi-byte UTF-8 characters are encoded byte-by-byte.
	for _, r := range value {
		if r < 0x80 {
			c := byte(r)
			if isAllowedUnencoded(c, kind) {
				sb.WriteByte(c)
				continue
			}
			writeHex(sb, c)
			continue
		}
		// Multi-byte UTF-8 — always percent-encode every byte.
		buf := []byte(string(r))
		for _, b := range buf {
			writeHex(sb, b)
		}
	}
}

// writeHex writes one %XX escape using upper-case hex digits.
func writeHex(sb *strings.Builder, b byte) {
	const hex = "0123456789ABCDEF"
	sb.WriteByte('%')
	sb.WriteByte(hex[b>>4])
	sb.WriteByte(hex[b&0x0F])
}

// isAllowedUnencoded reports whether c may be emitted literally in the given
// component. The allow-lists match the C# reference's EncodeKind table.
func isAllowedUnencoded(c byte, kind encodeKind) bool {
	if isUnreserved(c) {
		return true
	}
	switch kind {
	case encodePathSegment:
		// pchar = unreserved / pct-encoded / sub-delims / ":" / "@"
		return isSubDelim(c) || c == ':' || c == '@'
	case encodeQueryKey:
		// Always encode '&' and '=' in keys; allow ':' '@' and the sub-delims
		// that do not collide with query syntax.
		switch c {
		case ':', '@', '!', '$', '\'', '(', ')', '*', '+', ',', ';':
			return true
		}
		return false
	case encodeQueryValue:
		// Allow sub-delims except '&'; '=' is fine inside a value.
		switch c {
		case ':', '@', '/', '?', '!', '$', '\'', '(', ')', '*', '+', ',', ';', '=':
			return true
		}
		return false
	case encodeFragment:
		// fragment = *( pchar / "/" / "?" )  ; pchar incl. ':' '@' sub-delims
		return isSubDelim(c) || c == ':' || c == '@' || c == '/' || c == '?'
	}
	return false
}

// percentDecode decodes %XX escapes in s, treating bytes that fall outside
// any escape as their UTF-8 representation. Returns an error if a stray '%'
// is not followed by two hex digits.
func percentDecode(input string) (string, error) {
	if strings.IndexByte(input, '%') < 0 {
		return input, nil
	}
	buf := make([]byte, 0, len(input))
	for i := 0; i < len(input); i++ {
		c := input[i]
		if c == '%' {
			if i+2 >= len(input) || !isHexChar(input[i+1]) || !isHexChar(input[i+2]) {
				return "", fmt.Errorf("malformed percent-encoding at position %d", i)
			}
			buf = append(buf, byte((hexValue(input[i+1])<<4)|hexValue(input[i+2])))
			i += 2
			continue
		}
		// A non-escaped byte may be ASCII or part of a UTF-8 sequence;
		// emit it verbatim — Go strings are already UTF-8.
		buf = append(buf, c)
	}
	return string(buf), nil
}

// percentDecodePath decodes each '/'-separated segment independently so any
// %2F bytes inside a segment are preserved as literal '/' in the decoded
// segment, while the structural slashes between segments are not.
func percentDecodePath(path string) (string, error) {
	if path == "" {
		return path, nil
	}
	segs := strings.Split(path, "/")
	for i, s := range segs {
		decoded, err := percentDecode(s)
		if err != nil {
			return "", err
		}
		segs[i] = decoded
	}
	return strings.Join(segs, "/"), nil
}

