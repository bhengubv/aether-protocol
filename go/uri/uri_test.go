// SPDX-License-Identifier: MIT

package uri_test

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/bhengubv/aether-protocol/go/uri"
)

// ── corpus loading ───────────────────────────────────────────────────────────

// corpusFixture mirrors the JSON schema in tests/cross-language/uri-fixtures.json.
type corpusFixture struct {
	Schema      string `json:"$schema"`
	Description string `json:"description"`
	Version     string `json:"version"`
	Valid       []struct {
		Name         string            `json:"name"`
		Input        string            `json:"input"`
		Canonical    string            `json:"canonical"`
		Authority    string            `json:"authority"`
		Path         string            `json:"path"`
		HandlerName  string            `json:"handlerName"`
		PathSegments []string          `json:"pathSegments"`
		Query        map[string]string `json:"query"`
		Fragment     string            `json:"fragment"`
	} `json:"valid"`
	Invalid []struct {
		Name  string `json:"name"`
		Input string `json:"input"`
	} `json:"invalid"`
	Manifest struct {
		AppID    string `json:"appId"`
		Handlers []struct {
			HandlerName  string `json:"handlerName"`
			PathTemplate string `json:"pathTemplate"`
		} `json:"handlers"`
		Matches []struct {
			Input        string            `json:"input"`
			Matched      bool              `json:"matched"`
			HandlerIndex int               `json:"handlerIndex"`
			Captures     map[string]string `json:"captures"`
		} `json:"matches"`
	} `json:"manifest"`
}

// loadCorpus walks up from the test source dir until it finds
// tests/cross-language/uri-fixtures.json and parses it.
func loadCorpus(t *testing.T) *corpusFixture {
	t.Helper()
	dir, err := os.Getwd()
	if err != nil {
		t.Fatalf("os.Getwd: %v", err)
	}
	for {
		candidate := filepath.Join(dir, "tests", "cross-language", "uri-fixtures.json")
		if _, err := os.Stat(candidate); err == nil {
			data, err := os.ReadFile(candidate)
			if err != nil {
				t.Fatalf("read corpus %s: %v", candidate, err)
			}
			var c corpusFixture
			if err := json.Unmarshal(data, &c); err != nil {
				t.Fatalf("parse corpus %s: %v", candidate, err)
			}
			return &c
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			t.Fatalf("could not locate tests/cross-language/uri-fixtures.json walking up from %s", dir)
		}
		dir = parent
	}
}

// ── valid corpus ─────────────────────────────────────────────────────────────

func TestCorpus_ValidFixtures(t *testing.T) {
	c := loadCorpus(t)
	if len(c.Valid) == 0 {
		t.Fatal("no valid fixtures in corpus")
	}
	for _, fx := range c.Valid {
		fx := fx
		t.Run(fx.Name, func(t *testing.T) {
			u, err := uri.Parse(fx.Input)
			if err != nil {
				t.Fatalf("Parse(%q) error: %v", fx.Input, err)
			}
			if got := u.String(); got != fx.Canonical {
				t.Errorf("canonical mismatch:\n  got:  %q\n  want: %q", got, fx.Canonical)
			}
			if u.Authority != fx.Authority {
				t.Errorf("authority: got %q, want %q", u.Authority, fx.Authority)
			}
			if u.Path != fx.Path {
				t.Errorf("path: got %q, want %q", u.Path, fx.Path)
			}
			if u.HandlerName() != fx.HandlerName {
				t.Errorf("handlerName: got %q, want %q", u.HandlerName(), fx.HandlerName)
			}
			if u.Fragment != fx.Fragment {
				t.Errorf("fragment: got %q, want %q", u.Fragment, fx.Fragment)
			}
			if !stringSliceEqual(u.PathSegments(), fx.PathSegments) {
				t.Errorf("pathSegments: got %v, want %v", u.PathSegments(), fx.PathSegments)
			}
			if len(u.Query) != len(fx.Query) {
				t.Errorf("query size: got %d, want %d (got=%v, want=%v)",
					len(u.Query), len(fx.Query), u.Query, fx.Query)
			}
			for k, v := range fx.Query {
				if got, ok := u.Query[strings.ToLower(k)]; !ok || got != v {
					t.Errorf("query[%q]: got %q ok=%v, want %q", k, got, ok, v)
				}
			}
		})
	}
}

// ── invalid corpus ───────────────────────────────────────────────────────────

func TestCorpus_InvalidFixtures(t *testing.T) {
	c := loadCorpus(t)
	if len(c.Invalid) == 0 {
		t.Fatal("no invalid fixtures in corpus")
	}
	for _, fx := range c.Invalid {
		fx := fx
		t.Run(fx.Name, func(t *testing.T) {
			if u, err := uri.Parse(fx.Input); err == nil {
				t.Errorf("Parse(%q) unexpectedly succeeded: %+v", fx.Input, u)
			}
		})
	}
}

// ── manifest corpus ──────────────────────────────────────────────────────────

func TestCorpus_ManifestMatches(t *testing.T) {
	c := loadCorpus(t)
	if len(c.Manifest.Matches) == 0 {
		t.Fatal("no manifest fixtures in corpus")
	}
	handlers := make([]uri.HandlerDescriptor, 0, len(c.Manifest.Handlers))
	for _, h := range c.Manifest.Handlers {
		handlers = append(handlers, uri.HandlerDescriptor{
			Name:         h.HandlerName,
			PathTemplate: h.PathTemplate,
		})
	}
	m, err := uri.NewHandlerManifest(c.Manifest.AppID, handlers)
	if err != nil {
		t.Fatalf("NewHandlerManifest: %v", err)
	}
	for _, fx := range c.Manifest.Matches {
		fx := fx
		t.Run(fx.Input, func(t *testing.T) {
			u, err := uri.Parse(fx.Input)
			if err != nil {
				t.Fatalf("Parse(%q): %v", fx.Input, err)
			}
			resolved, ok := m.Resolve(u)
			if !fx.Matched {
				if ok {
					t.Errorf("Resolve(%q) matched unexpectedly: %+v", fx.Input, resolved)
				}
				return
			}
			if !ok {
				t.Fatalf("Resolve(%q) did not match", fx.Input)
			}
			expected := handlers[fx.HandlerIndex]
			if resolved.Handler.Name != expected.Name ||
				resolved.Handler.PathTemplate != expected.PathTemplate {
				t.Errorf("descriptor mismatch: got %+v, want %+v", resolved.Handler, expected)
			}
			if len(resolved.Captures) != len(fx.Captures) {
				t.Errorf("captures size: got %d, want %d (got=%v want=%v)",
					len(resolved.Captures), len(fx.Captures), resolved.Captures, fx.Captures)
			}
			for k, v := range fx.Captures {
				if got, ok := resolved.Captures[k]; !ok || got != v {
					t.Errorf("captures[%q]: got %q ok=%v, want %q", k, got, ok, v)
				}
			}
		})
	}
}

// ── hand-written parser tests ────────────────────────────────────────────────

func TestParse_AuthorityOnly(t *testing.T) {
	u, err := uri.Parse("aether://KXJB7-MN2P4")
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}
	if u.Authority != "KXJB7-MN2P4" {
		t.Errorf("authority: got %q", u.Authority)
	}
	if u.Path != "" || u.Fragment != "" || len(u.Query) != 0 {
		t.Errorf("expected empty path/query/fragment, got %+v", u)
	}
}

func TestParse_AuthorityWithoutDash_Canonicalises(t *testing.T) {
	u, err := uri.Parse("aether://KXJB7MN2P4")
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}
	if u.Authority != "KXJB7-MN2P4" {
		t.Errorf("authority: got %q want KXJB7-MN2P4", u.Authority)
	}
}

func TestParse_AuthorityLowercase_Canonicalises(t *testing.T) {
	u, err := uri.Parse("aether://kxjb7-mn2p4")
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}
	if u.Authority != "KXJB7-MN2P4" {
		t.Errorf("authority: got %q", u.Authority)
	}
}

func TestParse_SchemeCaseInsensitive(t *testing.T) {
	u, err := uri.Parse("AETHER://KXJB7-MN2P4/profile")
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}
	if got := u.String(); got != "aether://KXJB7-MN2P4/profile" {
		t.Errorf("canonical: got %q", got)
	}
}

func TestParse_QueryKey_IsCaseInsensitive(t *testing.T) {
	u, err := uri.Parse("aether://KXJB7-MN2P4/x?Codec=opus")
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}
	if v := u.Query["codec"]; v != "opus" {
		t.Errorf("query[codec]: got %q", v)
	}
}

func TestParse_FlagQuery(t *testing.T) {
	u, err := uri.Parse("aether://KXJB7-MN2P4/x?flag")
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}
	v, ok := u.Query["flag"]
	if !ok {
		t.Fatal("expected key flag to be present")
	}
	if v != "" {
		t.Errorf("flag value: got %q want \"\"", v)
	}
}

func TestParse_PercentEncodedUTF8(t *testing.T) {
	u, err := uri.Parse("aether://KXJB7-MN2P4/inbox?title=caf%C3%A9")
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}
	if u.Query["title"] != "café" {
		t.Errorf("title: got %q", u.Query["title"])
	}
}

func TestParse_UHID64Hex(t *testing.T) {
	hex := strings.Repeat("a", 64)
	u, err := uri.Parse("aether://" + hex + "/inbox")
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}
	if u.Authority != strings.ToUpper(hex) {
		t.Errorf("authority: got %q", u.Authority)
	}
	if u.HandlerName() != "inbox" {
		t.Errorf("handlerName: got %q", u.HandlerName())
	}
}

func TestParse_InvalidInputs(t *testing.T) {
	cases := []string{
		"",
		"http://KXJB7-MN2P4/",
		"aether:KXJB7-MN2P4",
		"aether:/KXJB7-MN2P4",
		"aether:///profile",
		"aether://INVALID-AUTH1/x",
		"aether://ABC",
		"aether://KXJB7-MN2P4/a//b",
		"aether://KXJB7-MN2P4/has space",
		"aether://KXJB7-MN2P4/inbox/%2",
		"aether://KXJB7-MN2P4/x?=value",
		"not-a-uri",
	}
	for _, in := range cases {
		in := in
		t.Run(in, func(t *testing.T) {
			if _, err := uri.Parse(in); err == nil {
				t.Errorf("Parse(%q) should have failed", in)
			}
		})
	}
}

func TestParse_ReturnsParseErrorType(t *testing.T) {
	_, err := uri.Parse("aether://INVALID-AUTH1/x")
	if err == nil {
		t.Fatal("expected error")
	}
	var pe *uri.ParseError
	if !errors.As(err, &pe) {
		t.Fatalf("expected *uri.ParseError, got %T", err)
	}
	if pe.Input == "" {
		t.Errorf("ParseError.Input is empty")
	}
	if pe.Msg == "" {
		t.Errorf("ParseError.Msg is empty")
	}
}

// ── round-trip ──────────────────────────────────────────────────────────────

func TestRoundTrip_CanonicalStable(t *testing.T) {
	inputs := []string{
		"aether://KXJB7-MN2P4",
		"aether://KXJB7-MN2P4/profile",
		"aether://KXJB7-MN2P4/content/sha256-abc",
		"aether://KXJB7-MN2P4/stream/live#t=1m30s",
	}
	for _, in := range inputs {
		in := in
		t.Run(in, func(t *testing.T) {
			parsed, err := uri.Parse(in)
			if err != nil {
				t.Fatalf("Parse: %v", err)
			}
			rendered := parsed.String()
			reparsed, err := uri.Parse(rendered)
			if err != nil {
				t.Fatalf("reparse: %v", err)
			}
			if !parsed.Equal(reparsed) {
				t.Errorf("not equal:\n  parsed:   %+v\n  reparsed: %+v", parsed, reparsed)
			}
			if rendered != reparsed.String() {
				t.Errorf("render mismatch:\n  first:  %q\n  second: %q", rendered, reparsed.String())
			}
		})
	}
}

// ── equality ────────────────────────────────────────────────────────────────

func TestEqual_SameContent(t *testing.T) {
	a, err := uri.Parse("aether://KXJB7-MN2P4/x?k=v")
	if err != nil {
		t.Fatal(err)
	}
	b, err := uri.Parse("aether://KXJB7-MN2P4/x?k=v")
	if err != nil {
		t.Fatal(err)
	}
	if !a.Equal(b) {
		t.Error("expected equal")
	}
}

func TestEqual_DifferentAuthority(t *testing.T) {
	a, _ := uri.Parse("aether://KXJB7-MN2P4/x")
	b, _ := uri.Parse("aether://KXJB7-MN2P5/x")
	if a.Equal(b) {
		t.Error("expected not equal")
	}
}

func TestEqual_QueryOrderIrrelevant(t *testing.T) {
	a, err := uri.Parse("aether://KXJB7-MN2P4/x?a=1&b=2")
	if err != nil {
		t.Fatal(err)
	}
	b, err := uri.Parse("aether://KXJB7-MN2P4/x?b=2&a=1")
	if err != nil {
		t.Fatal(err)
	}
	if !a.Equal(b) {
		t.Error("expected equal regardless of query order")
	}
}

func TestZeroURI_IsValidIsFalse(t *testing.T) {
	var u uri.URI
	if u.IsValid() {
		t.Error("zero URI should not be valid")
	}
	if got := u.String(); got != "" {
		t.Errorf("zero URI String: got %q want \"\"", got)
	}
}

// ── builder ──────────────────────────────────────────────────────────────────

func TestBuilder_FluentChain(t *testing.T) {
	u, err := uri.NewBuilder().
		Authority("KXJB7-MN2P4").
		Path("content/sha256-abc").
		Query("codec", "opus").
		Fragment("t=1m30s").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if got := u.String(); got != "aether://KXJB7-MN2P4/content/sha256-abc?codec=opus#t=1m30s" {
		t.Errorf("String: got %q", got)
	}
}

func TestBuilder_AppendPathSegment(t *testing.T) {
	u, err := uri.NewBuilder().
		Authority("KXJB7-MN2P4").
		AppendPathSegment("watch").
		AppendPathSegment("sess-99").
		AppendPathSegment("join").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if u.Path != "watch/sess-99/join" {
		t.Errorf("Path: got %q", u.Path)
	}
}

func TestBuilder_RemoveQuery(t *testing.T) {
	u, err := uri.NewBuilder().
		Authority("KXJB7-MN2P4").
		Path("x").
		Query("a", "1").
		Query("b", "2").
		RemoveQuery("a").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if _, present := u.Query["a"]; present {
		t.Error("key a should have been removed")
	}
	if u.Query["b"] != "2" {
		t.Errorf("Query[b]: got %q", u.Query["b"])
	}
}

func TestBuilder_StripLeadingSlash(t *testing.T) {
	u, err := uri.NewBuilder().
		Authority("KXJB7-MN2P4").
		Path("/profile").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if u.Path != "profile" {
		t.Errorf("Path: got %q", u.Path)
	}
}

func TestBuilder_StripLeadingHashOnFragment(t *testing.T) {
	u, err := uri.NewBuilder().
		Authority("KXJB7-MN2P4").
		Fragment("#anchor").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if u.Fragment != "anchor" {
		t.Errorf("Fragment: got %q", u.Fragment)
	}
}

func TestBuilder_MissingAuthority_Errors(t *testing.T) {
	_, err := uri.NewBuilder().Path("x").Build()
	if err == nil {
		t.Error("expected error from missing authority")
	}
}

func TestBuilder_BadAuthority_Errors(t *testing.T) {
	_, err := uri.NewBuilder().Authority("not-an-id").Build()
	if err == nil {
		t.Error("expected error from bad authority")
	}
}

func TestBuilder_EncodesSpaces(t *testing.T) {
	u, err := uri.NewBuilder().
		Authority("KXJB7-MN2P4").
		Path("inbox").
		Query("title", "hello world").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if !strings.Contains(u.String(), "hello%20world") {
		t.Errorf("expected hello%%20world in %q", u.String())
	}
}

// ── manifest ─────────────────────────────────────────────────────────────────

func sampleManifest(t *testing.T) *uri.HandlerManifest {
	t.Helper()
	m, err := uri.NewHandlerManifest("aether.media", []uri.HandlerDescriptor{
		{Name: "profile", Description: "Get the profile."},
		{Name: "profile", PathTemplate: "avatar", Description: "Get the avatar."},
		{Name: "content", PathTemplate: "{hash}", Description: "Fetch content."},
		{Name: "watch", PathTemplate: "{sessionId}/join", Description: "Join watch party."},
	})
	if err != nil {
		t.Fatalf("NewHandlerManifest: %v", err)
	}
	return m
}

func TestManifest_ExactMatchResolves(t *testing.T) {
	m := sampleManifest(t)
	u, _ := uri.Parse("aether://KXJB7-MN2P4/profile")
	r, ok := m.Resolve(u)
	if !ok {
		t.Fatal("expected match")
	}
	if r.Handler.Name != "profile" || r.Handler.PathTemplate != "" {
		t.Errorf("descriptor: got %+v", r.Handler)
	}
	if len(r.Captures) != 0 {
		t.Errorf("captures should be empty, got %v", r.Captures)
	}
}

func TestManifest_NestedExactMatchResolves(t *testing.T) {
	m := sampleManifest(t)
	u, _ := uri.Parse("aether://KXJB7-MN2P4/profile/avatar")
	r, ok := m.Resolve(u)
	if !ok {
		t.Fatal("expected match")
	}
	if r.Handler.PathTemplate != "avatar" {
		t.Errorf("PathTemplate: got %q", r.Handler.PathTemplate)
	}
}

func TestManifest_RouteCapture(t *testing.T) {
	m := sampleManifest(t)
	u, _ := uri.Parse("aether://KXJB7-MN2P4/content/sha256-abc")
	r, ok := m.Resolve(u)
	if !ok {
		t.Fatal("expected match")
	}
	if r.Captures["hash"] != "sha256-abc" {
		t.Errorf("hash: got %q", r.Captures["hash"])
	}
}

func TestManifest_MultiSegmentCapture(t *testing.T) {
	m := sampleManifest(t)
	u, _ := uri.Parse("aether://KXJB7-MN2P4/watch/sess-99/join")
	r, ok := m.Resolve(u)
	if !ok {
		t.Fatal("expected match")
	}
	if r.Captures["sessionId"] != "sess-99" {
		t.Errorf("sessionId: got %q", r.Captures["sessionId"])
	}
}

func TestManifest_UnknownHandler_NoMatch(t *testing.T) {
	m := sampleManifest(t)
	u, _ := uri.Parse("aether://KXJB7-MN2P4/unknown")
	if _, ok := m.Resolve(u); ok {
		t.Error("expected no match")
	}
}

func TestManifest_WrongPathLength_NoMatch(t *testing.T) {
	m := sampleManifest(t)
	u, _ := uri.Parse("aether://KXJB7-MN2P4/watch/sess-99")
	if _, ok := m.Resolve(u); ok {
		t.Error("expected no match")
	}
}

func TestManifest_EmptyAppID_Errors(t *testing.T) {
	if _, err := uri.NewHandlerManifest("", nil); err == nil {
		t.Error("expected error from empty AppID")
	}
}

func TestHandlerDescriptor_EmptyName_Errors(t *testing.T) {
	d := uri.HandlerDescriptor{Name: ""}
	if err := d.Validate(); err == nil {
		t.Error("expected validation error")
	}
}

// ── router ───────────────────────────────────────────────────────────────────

func TestRouter_Dispatch_InvokesCallback(t *testing.T) {
	m := sampleManifest(t)
	r, err := uri.NewRouter(m)
	if err != nil {
		t.Fatalf("NewRouter: %v", err)
	}
	invoked := false
	if err := r.Register(m.Handlers[0], func(_ context.Context, _ *uri.DispatchContext) error {
		invoked = true
		return nil
	}); err != nil {
		t.Fatalf("Register: %v", err)
	}
	ok, err := r.DispatchString(context.Background(), "aether://KXJB7-MN2P4/profile")
	if err != nil {
		t.Fatalf("Dispatch: %v", err)
	}
	if !ok {
		t.Error("expected Dispatch=true")
	}
	if !invoked {
		t.Error("expected callback to be invoked")
	}
}

func TestRouter_Dispatch_NoMatch_ReturnsFalse(t *testing.T) {
	m := sampleManifest(t)
	r, _ := uri.NewRouter(m)
	ok, err := r.DispatchString(context.Background(), "aether://KXJB7-MN2P4/nope")
	if err != nil {
		t.Fatalf("Dispatch: %v", err)
	}
	if ok {
		t.Error("expected no match")
	}
}

func TestRouter_Dispatch_ContextHasRouteParameters(t *testing.T) {
	m := sampleManifest(t)
	r, _ := uri.NewRouter(m)
	var seen *uri.DispatchContext
	if err := r.Register(m.Handlers[2], func(_ context.Context, dc *uri.DispatchContext) error {
		seen = dc
		return nil
	}); err != nil {
		t.Fatalf("Register: %v", err)
	}
	if _, err := r.DispatchString(context.Background(), "aether://KXJB7-MN2P4/content/sha256-xyz"); err != nil {
		t.Fatalf("Dispatch: %v", err)
	}
	if seen == nil {
		t.Fatal("expected context to be captured")
	}
	if seen.RouteParameters["hash"] != "sha256-xyz" {
		t.Errorf("hash: got %q", seen.RouteParameters["hash"])
	}
}

func TestRouter_Register_NotInManifest_Errors(t *testing.T) {
	m := sampleManifest(t)
	r, _ := uri.NewRouter(m)
	alien := uri.HandlerDescriptor{Name: "stranger"}
	if err := r.Register(alien, func(_ context.Context, _ *uri.DispatchContext) error { return nil }); err == nil {
		t.Error("expected error registering descriptor not in manifest")
	}
}

func TestRouter_Dispatch_NoCallbackRegistered_ReturnsFalse(t *testing.T) {
	m := sampleManifest(t)
	r, _ := uri.NewRouter(m)
	// /profile is in the manifest but no callback registered.
	ok, err := r.DispatchString(context.Background(), "aether://KXJB7-MN2P4/profile")
	if err != nil {
		t.Fatalf("Dispatch: %v", err)
	}
	if ok {
		t.Error("expected false when no callback registered")
	}
}

func TestRouter_Dispatch_PropagatesHandlerError(t *testing.T) {
	m := sampleManifest(t)
	r, _ := uri.NewRouter(m)
	boom := errors.New("boom")
	if err := r.Register(m.Handlers[0], func(_ context.Context, _ *uri.DispatchContext) error {
		return boom
	}); err != nil {
		t.Fatalf("Register: %v", err)
	}
	ok, err := r.DispatchString(context.Background(), "aether://KXJB7-MN2P4/profile")
	if !errors.Is(err, boom) {
		t.Errorf("expected boom error, got %v", err)
	}
	if ok {
		t.Error("expected ok=false when handler errors")
	}
}

func TestRouter_DispatchString_ParseError_Returned(t *testing.T) {
	m := sampleManifest(t)
	r, _ := uri.NewRouter(m)
	_, err := r.DispatchString(context.Background(), "not-a-uri")
	if err == nil {
		t.Error("expected parse error")
	}
	var pe *uri.ParseError
	if !errors.As(err, &pe) {
		t.Errorf("expected *uri.ParseError, got %T", err)
	}
}

func TestRouter_NewRouter_NilManifest_Errors(t *testing.T) {
	if _, err := uri.NewRouter(nil); err == nil {
		t.Error("expected error from nil manifest")
	}
}

// ── helpers ──────────────────────────────────────────────────────────────────

func stringSliceEqual(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

