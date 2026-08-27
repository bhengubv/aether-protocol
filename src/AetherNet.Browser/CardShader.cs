// SPDX-License-Identifier: MIT

namespace AetherNet.Browser;

/// <summary>
/// The catalogue of backgrounds a card can wear.
///
/// <para>
/// <b>Why a catalogue rather than a shader.</b> What separates a page somebody is proud of from a page
/// they settle for is usually not skill — it is how many finished things there were to choose from. A
/// library with one background produces one look; a library with forty produces pages its author could
/// never have designed. Nobody browsing a component library writes shaders either. They pick.
/// </para>
///
/// <para>
/// <b>An entry is a height field and nothing else.</b> Four to ten lines of GLSL returning a number for
/// a point on the plate. The painter does the rest — takes the normal by difference, lights it, and
/// mixes the card's own accent toward white — so every background in the catalogue lands in the same
/// family however it was written, and takes on the colour of whatever page it is on.
/// </para>
///
/// <para>
/// That is what makes this something to build on. Adding a background is adding one record with one
/// function in it; the renderer, the editor, the thumbnails and the picker all pick it up with no
/// further change. A developer's first contribution to this library can be five lines long.
/// </para>
///
/// <para>
/// <b>Nothing an author writes reaches the GPU.</b> A card names a background by key; the key is looked
/// up here and the source that gets inlined is source we shipped. An unknown key falls back to the
/// default, the way an unknown look and an unknown block already do.
/// </para>
/// </summary>
/// <param name="Key">What a card names to ask for this background.</param>
/// <param name="Name">What a person choosing it reads.</param>
/// <param name="Blurb">What it looks like, in a few words.</param>
/// <param name="Field">
///   GLSL. Must define <c>float field(vec2 p)</c>, where <c>p</c> is the plate in units of its own
///   height with x widened by the aspect. <c>u_t</c> is seconds, <c>u_seed</c> is a per-page number
///   near 0..10. Return roughly -1..1; the painter lights whatever comes back.
/// </param>
public sealed record CardShader(string Key, string Name, string Blurb, string Field)
{
    /// <summary>What a card gets when it asks for nothing.</summary>
    public const string DefaultKey = "rings";

    /// <summary>Every background this library ships, in the order the editor offers them.</summary>
    public static readonly CardShader[] All =
    [
        new("rings", "Rings", "Two interfering ripples. Calm, and never quite repeating.",
            """
            float field(vec2 p){
              vec2 a = p - vec2(0.24 + 0.20*sin(u_seed), 0.34 + 0.16*cos(u_seed*1.7));
              vec2 b = p - vec2(0.82 + 0.12*cos(u_seed*2.3), 0.18 + 0.22*sin(u_seed*0.9));
              float folds = 3.0 + floor(mod(u_seed*3.0, 4.0));
              float wa = sin(length(a*vec2(1.0,1.35))*26.0 - u_t*0.55 + sin(atan(a.y,a.x)*folds)*0.85);
              float wb = sin(length(b*vec2(1.25,1.0))*19.0 + u_t*0.37 + cos(atan(b.y,b.x)*(folds+2.0))*0.70);
              return wa*0.62 + wb*0.48;
            }
            """),

        new("ribbon", "Ribbon", "A single lit band arcing across the plate.",
            """
            float field(vec2 p){
              float arc = 0.5 + 0.26*sin(p.x*2.1 + u_seed) - 0.12*cos(p.x*3.7 - u_t*0.2);
              float d = (p.y - arc) * 5.2;
              return sin(d*3.0 - u_t*0.5) * exp(-d*d*0.6);
            }
            """),

        new("halftone", "Halftone", "A dot grid bending through a slow current.",
            """
            float field(vec2 p){
              vec2 w = p + 0.12*vec2(sin(p.y*4.0 + u_t*0.3 + u_seed), cos(p.x*4.0 - u_t*0.22));
              vec2 c = fract(w*22.0) - 0.5;
              float dot = 0.5 - length(c);
              return dot*2.4 + 0.25*sin(w.x*6.0 + w.y*4.0 - u_t*0.3);
            }
            """),

        new("void", "Void", "A grid receding into the middle distance.",
            """
            float field(vec2 p){
              vec2 c = p - vec2(0.5 + 0.1*sin(u_seed), 0.5);
              float r = length(c*vec2(1.0,1.3)) + 0.001;
              vec2 g = vec2(atan(c.y,c.x)*2.2, log(r)*2.4 + u_t*0.12);
              vec2 f = abs(fract(g*3.0) - 0.5);
              return (0.5 - min(f.x,f.y)*2.0) * smoothstep(0.0, 0.45, r);
            }
            """),

        new("flow", "Flow", "Liquid metal, folded slowly.",
            """
            float field(vec2 p){
              vec2 q = p*2.4;
              for (int i = 0; i < 3; i++) {
                q += 0.55*vec2(sin(q.y*1.7 + u_t*0.21 + u_seed), cos(q.x*1.5 - u_t*0.17));
              }
              return sin(q.x + q.y)*0.7 + sin(q.x*1.7 - q.y*0.9)*0.4;
            }
            """),

        new("weave", "Weave", "Two rulings crossing. Close, technical, fabric-like.",
            """
            float field(vec2 p){
              float a = sin((p.x*cos(0.5) + p.y*sin(0.5))*54.0 + u_seed);
              float b = sin((p.x*cos(-0.7) + p.y*sin(-0.7))*44.0 - u_t*0.25);
              return a*0.55 + b*0.55 + 0.2*sin(p.x*3.0 + p.y*2.0 - u_t*0.15);
            }
            """),

        new("dunes", "Dunes", "Long ridges lit from one side.",
            """
            float field(vec2 p){
              float h = 0.0, amp = 0.62, f = 2.2;
              for (int i = 0; i < 4; i++) {
                h += amp * sin(p.x*f + sin(p.y*f*0.6 + u_seed)*1.4 - u_t*0.12);
                amp *= 0.52; f *= 1.9;
              }
              return h;
            }
            """),

        new("prism", "Prism", "Hard angular bands, cut rather than drawn.",
            """
            float field(vec2 p){
              float a = 0.7 + 0.25*sin(u_seed);
              float u = p.x*cos(a) + p.y*sin(a);
              float band = fract(u*7.0 + u_t*0.05);
              return (band < 0.5 ? band : 1.0 - band) * 3.4 - 0.85;
            }
            """),

        new("orbit", "Orbit", "Ellipses sliding past one another.",
            """
            float field(vec2 p){
              float s = 0.0;
              for (int i = 0; i < 3; i++) {
                float k = float(i) + 1.0;
                vec2 c = vec2(0.5 + 0.22*sin(u_seed*k), 0.5 + 0.16*cos(u_t*0.11*k + u_seed));
                s += sin(length((p - c)*vec2(1.0 + 0.3*k, 1.0))*(16.0 + 6.0*k) - u_t*0.3)/k;
              }
              return s*0.8;
            }
            """),

        new("tide", "Tide", "Horizontal swell. Quiet, and good under a title.",
            """
            float field(vec2 p){
              float y = p.y*8.0;
              float swell = sin(p.x*2.0 + u_t*0.16 + u_seed)*0.6 + sin(p.x*4.7 - u_t*0.1)*0.25;
              return sin(y + swell*2.4) * (0.5 + 0.5*sin(p.x*1.3 + u_seed));
            }
            """),

        new("thread", "Thread", "Fine parallel lines pulled out of true.",
            """
            float field(vec2 p){
              float warp = 0.16*sin(p.x*3.1 + u_t*0.18 + u_seed) + 0.09*cos(p.x*6.3 - u_t*0.11);
              return sin((p.y + warp)*88.0)*0.85;
            }
            """),

        new("pulse", "Pulse", "Rings leaving a point, slowly.",
            """
            float field(vec2 p){
              vec2 c = vec2(0.28 + 0.3*sin(u_seed), 0.62);
              float r = length((p - c)*vec2(1.0, 1.25));
              return sin(r*30.0 - u_t*1.1) * exp(-r*1.5);
            }
            """),

        new("facet", "Facet", "Broken into planes, like cut stone.",
            """
            float field(vec2 p){
              vec2 g = floor(p*6.0 + u_seed);
              vec2 f = fract(p*6.0 + u_seed);
              float best = 8.0;
              for (int y = -1; y <= 1; y++) {
                for (int x = -1; x <= 1; x++) {
                  vec2 o = vec2(float(x), float(y));
                  vec2 seed = g + o;
                  vec2 jitter = fract(sin(vec2(dot(seed, vec2(12.99, 78.23)), dot(seed, vec2(39.35, 11.13))))*43758.5453);
                  jitter = 0.5 + 0.42*sin(u_t*0.2 + 6.28*jitter);
                  best = min(best, length(o + jitter - f));
                }
              }
              return 1.0 - best*1.9;
            }
            """),

        new("grain", "Grain", "Almost flat. A gradient with a tooth to it.",
            """
            float field(vec2 p){
              float n = fract(sin(dot(floor(p*260.0), vec2(12.9898, 78.233)))*43758.5453);
              float soft = sin(p.x*1.6 + u_seed)*0.4 + cos(p.y*2.1 - u_t*0.07)*0.3;
              return soft + (n - 0.5)*0.35;
            }
            """),
    ];

    /// <summary>The background with this key, or the default.</summary>
    /// <remarks>
    /// An unknown key is a newer author, not a broken card — the same rule the block model and the
    /// looks already follow. A page written on a build that has more backgrounds than this one still
    /// renders, in the default, with every word intact.
    /// </remarks>
    public static CardShader Of(string? key)
    {
        var wanted = key?.Trim().ToLowerInvariant();
        return All.FirstOrDefault(s => s.Key == wanted) ?? All.First(s => s.Key == DefaultKey);
    }

    /// <summary>Whether this is a background we ship.</summary>
    public static bool IsShader(string? key) =>
        key is not null && All.Any(s => s.Key == key.Trim().ToLowerInvariant());

    /// <summary>The background a card asked for, read from its theme blocks.</summary>
    public static CardShader FromCard(CardDocument? card)
    {
        var asked = card?.Blocks?
            .FirstOrDefault(b => b.Kind == CardBlock.Theme && IsShader(b.Value))?
            .Value;

        return Of(asked);
    }
}
