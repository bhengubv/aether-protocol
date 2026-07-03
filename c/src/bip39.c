// SPDX-License-Identifier: MIT
// BIP-39 recovery-phrase backup for an AetherNet identity.
//
// Real, standard BIP-39 over the official 2048-word English wordlist. Verified
// against the Trezor test vectors (fixtures/bip39/vectors.json); a phrase
// produced here restores on any conformant BIP-39 wallet, and every AetherNet
// language SDK reproduces the same words and seed byte-for-byte. This C port is
// the faithful mirror of src/AetherNet.Security/Backup/Bip39Mnemonic.cs and
// IdentityBackup.cs.
//
// Crypto primitives reuse the SDK's existing libsodium backend (the same one
// src/security.c uses): crypto_hash_sha256 for the checksum, the streaming
// crypto_auth_hmacsha512 for PBKDF2, and crypto_sign_ed25519_seed_keypair for
// identity public-key derivation. Only PBKDF2-HMAC-SHA512 is new (libsodium
// exposes no PBKDF2), implemented below in terms of HMAC-SHA512.

#if !defined(_WIN32) && !defined(_POSIX_C_SOURCE)
#  define _POSIX_C_SOURCE 200809L
#endif

#include <stdlib.h>
#include <string.h>

#include "aethernet/bip39.h"

#include <sodium.h>

/* ─── Embedded official BIP-39 English wordlist ─────────────────────────────
 * The exact 2048 words from fixtures/bip39/english.txt (lines "abandon".."zoo",
 * SHA-256 2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda),
 * generated directly from that file — never hand-edited. Same static-table
 * embedding style as the other large constant tables in this library.        */
const char *const aethernet_bip39_wordlist[AETHERNET_BIP39_WORDLIST_SIZE] = {
    "abandon", "ability", "able", "about",
    "above", "absent", "absorb", "abstract",
    "absurd", "abuse", "access", "accident",
    "account", "accuse", "achieve", "acid",
    "acoustic", "acquire", "across", "act",
    "action", "actor", "actress", "actual",
    "adapt", "add", "addict", "address",
    "adjust", "admit", "adult", "advance",
    "advice", "aerobic", "affair", "afford",
    "afraid", "again", "age", "agent",
    "agree", "ahead", "aim", "air",
    "airport", "aisle", "alarm", "album",
    "alcohol", "alert", "alien", "all",
    "alley", "allow", "almost", "alone",
    "alpha", "already", "also", "alter",
    "always", "amateur", "amazing", "among",
    "amount", "amused", "analyst", "anchor",
    "ancient", "anger", "angle", "angry",
    "animal", "ankle", "announce", "annual",
    "another", "answer", "antenna", "antique",
    "anxiety", "any", "apart", "apology",
    "appear", "apple", "approve", "april",
    "arch", "arctic", "area", "arena",
    "argue", "arm", "armed", "armor",
    "army", "around", "arrange", "arrest",
    "arrive", "arrow", "art", "artefact",
    "artist", "artwork", "ask", "aspect",
    "assault", "asset", "assist", "assume",
    "asthma", "athlete", "atom", "attack",
    "attend", "attitude", "attract", "auction",
    "audit", "august", "aunt", "author",
    "auto", "autumn", "average", "avocado",
    "avoid", "awake", "aware", "away",
    "awesome", "awful", "awkward", "axis",
    "baby", "bachelor", "bacon", "badge",
    "bag", "balance", "balcony", "ball",
    "bamboo", "banana", "banner", "bar",
    "barely", "bargain", "barrel", "base",
    "basic", "basket", "battle", "beach",
    "bean", "beauty", "because", "become",
    "beef", "before", "begin", "behave",
    "behind", "believe", "below", "belt",
    "bench", "benefit", "best", "betray",
    "better", "between", "beyond", "bicycle",
    "bid", "bike", "bind", "biology",
    "bird", "birth", "bitter", "black",
    "blade", "blame", "blanket", "blast",
    "bleak", "bless", "blind", "blood",
    "blossom", "blouse", "blue", "blur",
    "blush", "board", "boat", "body",
    "boil", "bomb", "bone", "bonus",
    "book", "boost", "border", "boring",
    "borrow", "boss", "bottom", "bounce",
    "box", "boy", "bracket", "brain",
    "brand", "brass", "brave", "bread",
    "breeze", "brick", "bridge", "brief",
    "bright", "bring", "brisk", "broccoli",
    "broken", "bronze", "broom", "brother",
    "brown", "brush", "bubble", "buddy",
    "budget", "buffalo", "build", "bulb",
    "bulk", "bullet", "bundle", "bunker",
    "burden", "burger", "burst", "bus",
    "business", "busy", "butter", "buyer",
    "buzz", "cabbage", "cabin", "cable",
    "cactus", "cage", "cake", "call",
    "calm", "camera", "camp", "can",
    "canal", "cancel", "candy", "cannon",
    "canoe", "canvas", "canyon", "capable",
    "capital", "captain", "car", "carbon",
    "card", "cargo", "carpet", "carry",
    "cart", "case", "cash", "casino",
    "castle", "casual", "cat", "catalog",
    "catch", "category", "cattle", "caught",
    "cause", "caution", "cave", "ceiling",
    "celery", "cement", "census", "century",
    "cereal", "certain", "chair", "chalk",
    "champion", "change", "chaos", "chapter",
    "charge", "chase", "chat", "cheap",
    "check", "cheese", "chef", "cherry",
    "chest", "chicken", "chief", "child",
    "chimney", "choice", "choose", "chronic",
    "chuckle", "chunk", "churn", "cigar",
    "cinnamon", "circle", "citizen", "city",
    "civil", "claim", "clap", "clarify",
    "claw", "clay", "clean", "clerk",
    "clever", "click", "client", "cliff",
    "climb", "clinic", "clip", "clock",
    "clog", "close", "cloth", "cloud",
    "clown", "club", "clump", "cluster",
    "clutch", "coach", "coast", "coconut",
    "code", "coffee", "coil", "coin",
    "collect", "color", "column", "combine",
    "come", "comfort", "comic", "common",
    "company", "concert", "conduct", "confirm",
    "congress", "connect", "consider", "control",
    "convince", "cook", "cool", "copper",
    "copy", "coral", "core", "corn",
    "correct", "cost", "cotton", "couch",
    "country", "couple", "course", "cousin",
    "cover", "coyote", "crack", "cradle",
    "craft", "cram", "crane", "crash",
    "crater", "crawl", "crazy", "cream",
    "credit", "creek", "crew", "cricket",
    "crime", "crisp", "critic", "crop",
    "cross", "crouch", "crowd", "crucial",
    "cruel", "cruise", "crumble", "crunch",
    "crush", "cry", "crystal", "cube",
    "culture", "cup", "cupboard", "curious",
    "current", "curtain", "curve", "cushion",
    "custom", "cute", "cycle", "dad",
    "damage", "damp", "dance", "danger",
    "daring", "dash", "daughter", "dawn",
    "day", "deal", "debate", "debris",
    "decade", "december", "decide", "decline",
    "decorate", "decrease", "deer", "defense",
    "define", "defy", "degree", "delay",
    "deliver", "demand", "demise", "denial",
    "dentist", "deny", "depart", "depend",
    "deposit", "depth", "deputy", "derive",
    "describe", "desert", "design", "desk",
    "despair", "destroy", "detail", "detect",
    "develop", "device", "devote", "diagram",
    "dial", "diamond", "diary", "dice",
    "diesel", "diet", "differ", "digital",
    "dignity", "dilemma", "dinner", "dinosaur",
    "direct", "dirt", "disagree", "discover",
    "disease", "dish", "dismiss", "disorder",
    "display", "distance", "divert", "divide",
    "divorce", "dizzy", "doctor", "document",
    "dog", "doll", "dolphin", "domain",
    "donate", "donkey", "donor", "door",
    "dose", "double", "dove", "draft",
    "dragon", "drama", "drastic", "draw",
    "dream", "dress", "drift", "drill",
    "drink", "drip", "drive", "drop",
    "drum", "dry", "duck", "dumb",
    "dune", "during", "dust", "dutch",
    "duty", "dwarf", "dynamic", "eager",
    "eagle", "early", "earn", "earth",
    "easily", "east", "easy", "echo",
    "ecology", "economy", "edge", "edit",
    "educate", "effort", "egg", "eight",
    "either", "elbow", "elder", "electric",
    "elegant", "element", "elephant", "elevator",
    "elite", "else", "embark", "embody",
    "embrace", "emerge", "emotion", "employ",
    "empower", "empty", "enable", "enact",
    "end", "endless", "endorse", "enemy",
    "energy", "enforce", "engage", "engine",
    "enhance", "enjoy", "enlist", "enough",
    "enrich", "enroll", "ensure", "enter",
    "entire", "entry", "envelope", "episode",
    "equal", "equip", "era", "erase",
    "erode", "erosion", "error", "erupt",
    "escape", "essay", "essence", "estate",
    "eternal", "ethics", "evidence", "evil",
    "evoke", "evolve", "exact", "example",
    "excess", "exchange", "excite", "exclude",
    "excuse", "execute", "exercise", "exhaust",
    "exhibit", "exile", "exist", "exit",
    "exotic", "expand", "expect", "expire",
    "explain", "expose", "express", "extend",
    "extra", "eye", "eyebrow", "fabric",
    "face", "faculty", "fade", "faint",
    "faith", "fall", "false", "fame",
    "family", "famous", "fan", "fancy",
    "fantasy", "farm", "fashion", "fat",
    "fatal", "father", "fatigue", "fault",
    "favorite", "feature", "february", "federal",
    "fee", "feed", "feel", "female",
    "fence", "festival", "fetch", "fever",
    "few", "fiber", "fiction", "field",
    "figure", "file", "film", "filter",
    "final", "find", "fine", "finger",
    "finish", "fire", "firm", "first",
    "fiscal", "fish", "fit", "fitness",
    "fix", "flag", "flame", "flash",
    "flat", "flavor", "flee", "flight",
    "flip", "float", "flock", "floor",
    "flower", "fluid", "flush", "fly",
    "foam", "focus", "fog", "foil",
    "fold", "follow", "food", "foot",
    "force", "forest", "forget", "fork",
    "fortune", "forum", "forward", "fossil",
    "foster", "found", "fox", "fragile",
    "frame", "frequent", "fresh", "friend",
    "fringe", "frog", "front", "frost",
    "frown", "frozen", "fruit", "fuel",
    "fun", "funny", "furnace", "fury",
    "future", "gadget", "gain", "galaxy",
    "gallery", "game", "gap", "garage",
    "garbage", "garden", "garlic", "garment",
    "gas", "gasp", "gate", "gather",
    "gauge", "gaze", "general", "genius",
    "genre", "gentle", "genuine", "gesture",
    "ghost", "giant", "gift", "giggle",
    "ginger", "giraffe", "girl", "give",
    "glad", "glance", "glare", "glass",
    "glide", "glimpse", "globe", "gloom",
    "glory", "glove", "glow", "glue",
    "goat", "goddess", "gold", "good",
    "goose", "gorilla", "gospel", "gossip",
    "govern", "gown", "grab", "grace",
    "grain", "grant", "grape", "grass",
    "gravity", "great", "green", "grid",
    "grief", "grit", "grocery", "group",
    "grow", "grunt", "guard", "guess",
    "guide", "guilt", "guitar", "gun",
    "gym", "habit", "hair", "half",
    "hammer", "hamster", "hand", "happy",
    "harbor", "hard", "harsh", "harvest",
    "hat", "have", "hawk", "hazard",
    "head", "health", "heart", "heavy",
    "hedgehog", "height", "hello", "helmet",
    "help", "hen", "hero", "hidden",
    "high", "hill", "hint", "hip",
    "hire", "history", "hobby", "hockey",
    "hold", "hole", "holiday", "hollow",
    "home", "honey", "hood", "hope",
    "horn", "horror", "horse", "hospital",
    "host", "hotel", "hour", "hover",
    "hub", "huge", "human", "humble",
    "humor", "hundred", "hungry", "hunt",
    "hurdle", "hurry", "hurt", "husband",
    "hybrid", "ice", "icon", "idea",
    "identify", "idle", "ignore", "ill",
    "illegal", "illness", "image", "imitate",
    "immense", "immune", "impact", "impose",
    "improve", "impulse", "inch", "include",
    "income", "increase", "index", "indicate",
    "indoor", "industry", "infant", "inflict",
    "inform", "inhale", "inherit", "initial",
    "inject", "injury", "inmate", "inner",
    "innocent", "input", "inquiry", "insane",
    "insect", "inside", "inspire", "install",
    "intact", "interest", "into", "invest",
    "invite", "involve", "iron", "island",
    "isolate", "issue", "item", "ivory",
    "jacket", "jaguar", "jar", "jazz",
    "jealous", "jeans", "jelly", "jewel",
    "job", "join", "joke", "journey",
    "joy", "judge", "juice", "jump",
    "jungle", "junior", "junk", "just",
    "kangaroo", "keen", "keep", "ketchup",
    "key", "kick", "kid", "kidney",
    "kind", "kingdom", "kiss", "kit",
    "kitchen", "kite", "kitten", "kiwi",
    "knee", "knife", "knock", "know",
    "lab", "label", "labor", "ladder",
    "lady", "lake", "lamp", "language",
    "laptop", "large", "later", "latin",
    "laugh", "laundry", "lava", "law",
    "lawn", "lawsuit", "layer", "lazy",
    "leader", "leaf", "learn", "leave",
    "lecture", "left", "leg", "legal",
    "legend", "leisure", "lemon", "lend",
    "length", "lens", "leopard", "lesson",
    "letter", "level", "liar", "liberty",
    "library", "license", "life", "lift",
    "light", "like", "limb", "limit",
    "link", "lion", "liquid", "list",
    "little", "live", "lizard", "load",
    "loan", "lobster", "local", "lock",
    "logic", "lonely", "long", "loop",
    "lottery", "loud", "lounge", "love",
    "loyal", "lucky", "luggage", "lumber",
    "lunar", "lunch", "luxury", "lyrics",
    "machine", "mad", "magic", "magnet",
    "maid", "mail", "main", "major",
    "make", "mammal", "man", "manage",
    "mandate", "mango", "mansion", "manual",
    "maple", "marble", "march", "margin",
    "marine", "market", "marriage", "mask",
    "mass", "master", "match", "material",
    "math", "matrix", "matter", "maximum",
    "maze", "meadow", "mean", "measure",
    "meat", "mechanic", "medal", "media",
    "melody", "melt", "member", "memory",
    "mention", "menu", "mercy", "merge",
    "merit", "merry", "mesh", "message",
    "metal", "method", "middle", "midnight",
    "milk", "million", "mimic", "mind",
    "minimum", "minor", "minute", "miracle",
    "mirror", "misery", "miss", "mistake",
    "mix", "mixed", "mixture", "mobile",
    "model", "modify", "mom", "moment",
    "monitor", "monkey", "monster", "month",
    "moon", "moral", "more", "morning",
    "mosquito", "mother", "motion", "motor",
    "mountain", "mouse", "move", "movie",
    "much", "muffin", "mule", "multiply",
    "muscle", "museum", "mushroom", "music",
    "must", "mutual", "myself", "mystery",
    "myth", "naive", "name", "napkin",
    "narrow", "nasty", "nation", "nature",
    "near", "neck", "need", "negative",
    "neglect", "neither", "nephew", "nerve",
    "nest", "net", "network", "neutral",
    "never", "news", "next", "nice",
    "night", "noble", "noise", "nominee",
    "noodle", "normal", "north", "nose",
    "notable", "note", "nothing", "notice",
    "novel", "now", "nuclear", "number",
    "nurse", "nut", "oak", "obey",
    "object", "oblige", "obscure", "observe",
    "obtain", "obvious", "occur", "ocean",
    "october", "odor", "off", "offer",
    "office", "often", "oil", "okay",
    "old", "olive", "olympic", "omit",
    "once", "one", "onion", "online",
    "only", "open", "opera", "opinion",
    "oppose", "option", "orange", "orbit",
    "orchard", "order", "ordinary", "organ",
    "orient", "original", "orphan", "ostrich",
    "other", "outdoor", "outer", "output",
    "outside", "oval", "oven", "over",
    "own", "owner", "oxygen", "oyster",
    "ozone", "pact", "paddle", "page",
    "pair", "palace", "palm", "panda",
    "panel", "panic", "panther", "paper",
    "parade", "parent", "park", "parrot",
    "party", "pass", "patch", "path",
    "patient", "patrol", "pattern", "pause",
    "pave", "payment", "peace", "peanut",
    "pear", "peasant", "pelican", "pen",
    "penalty", "pencil", "people", "pepper",
    "perfect", "permit", "person", "pet",
    "phone", "photo", "phrase", "physical",
    "piano", "picnic", "picture", "piece",
    "pig", "pigeon", "pill", "pilot",
    "pink", "pioneer", "pipe", "pistol",
    "pitch", "pizza", "place", "planet",
    "plastic", "plate", "play", "please",
    "pledge", "pluck", "plug", "plunge",
    "poem", "poet", "point", "polar",
    "pole", "police", "pond", "pony",
    "pool", "popular", "portion", "position",
    "possible", "post", "potato", "pottery",
    "poverty", "powder", "power", "practice",
    "praise", "predict", "prefer", "prepare",
    "present", "pretty", "prevent", "price",
    "pride", "primary", "print", "priority",
    "prison", "private", "prize", "problem",
    "process", "produce", "profit", "program",
    "project", "promote", "proof", "property",
    "prosper", "protect", "proud", "provide",
    "public", "pudding", "pull", "pulp",
    "pulse", "pumpkin", "punch", "pupil",
    "puppy", "purchase", "purity", "purpose",
    "purse", "push", "put", "puzzle",
    "pyramid", "quality", "quantum", "quarter",
    "question", "quick", "quit", "quiz",
    "quote", "rabbit", "raccoon", "race",
    "rack", "radar", "radio", "rail",
    "rain", "raise", "rally", "ramp",
    "ranch", "random", "range", "rapid",
    "rare", "rate", "rather", "raven",
    "raw", "razor", "ready", "real",
    "reason", "rebel", "rebuild", "recall",
    "receive", "recipe", "record", "recycle",
    "reduce", "reflect", "reform", "refuse",
    "region", "regret", "regular", "reject",
    "relax", "release", "relief", "rely",
    "remain", "remember", "remind", "remove",
    "render", "renew", "rent", "reopen",
    "repair", "repeat", "replace", "report",
    "require", "rescue", "resemble", "resist",
    "resource", "response", "result", "retire",
    "retreat", "return", "reunion", "reveal",
    "review", "reward", "rhythm", "rib",
    "ribbon", "rice", "rich", "ride",
    "ridge", "rifle", "right", "rigid",
    "ring", "riot", "ripple", "risk",
    "ritual", "rival", "river", "road",
    "roast", "robot", "robust", "rocket",
    "romance", "roof", "rookie", "room",
    "rose", "rotate", "rough", "round",
    "route", "royal", "rubber", "rude",
    "rug", "rule", "run", "runway",
    "rural", "sad", "saddle", "sadness",
    "safe", "sail", "salad", "salmon",
    "salon", "salt", "salute", "same",
    "sample", "sand", "satisfy", "satoshi",
    "sauce", "sausage", "save", "say",
    "scale", "scan", "scare", "scatter",
    "scene", "scheme", "school", "science",
    "scissors", "scorpion", "scout", "scrap",
    "screen", "script", "scrub", "sea",
    "search", "season", "seat", "second",
    "secret", "section", "security", "seed",
    "seek", "segment", "select", "sell",
    "seminar", "senior", "sense", "sentence",
    "series", "service", "session", "settle",
    "setup", "seven", "shadow", "shaft",
    "shallow", "share", "shed", "shell",
    "sheriff", "shield", "shift", "shine",
    "ship", "shiver", "shock", "shoe",
    "shoot", "shop", "short", "shoulder",
    "shove", "shrimp", "shrug", "shuffle",
    "shy", "sibling", "sick", "side",
    "siege", "sight", "sign", "silent",
    "silk", "silly", "silver", "similar",
    "simple", "since", "sing", "siren",
    "sister", "situate", "six", "size",
    "skate", "sketch", "ski", "skill",
    "skin", "skirt", "skull", "slab",
    "slam", "sleep", "slender", "slice",
    "slide", "slight", "slim", "slogan",
    "slot", "slow", "slush", "small",
    "smart", "smile", "smoke", "smooth",
    "snack", "snake", "snap", "sniff",
    "snow", "soap", "soccer", "social",
    "sock", "soda", "soft", "solar",
    "soldier", "solid", "solution", "solve",
    "someone", "song", "soon", "sorry",
    "sort", "soul", "sound", "soup",
    "source", "south", "space", "spare",
    "spatial", "spawn", "speak", "special",
    "speed", "spell", "spend", "sphere",
    "spice", "spider", "spike", "spin",
    "spirit", "split", "spoil", "sponsor",
    "spoon", "sport", "spot", "spray",
    "spread", "spring", "spy", "square",
    "squeeze", "squirrel", "stable", "stadium",
    "staff", "stage", "stairs", "stamp",
    "stand", "start", "state", "stay",
    "steak", "steel", "stem", "step",
    "stereo", "stick", "still", "sting",
    "stock", "stomach", "stone", "stool",
    "story", "stove", "strategy", "street",
    "strike", "strong", "struggle", "student",
    "stuff", "stumble", "style", "subject",
    "submit", "subway", "success", "such",
    "sudden", "suffer", "sugar", "suggest",
    "suit", "summer", "sun", "sunny",
    "sunset", "super", "supply", "supreme",
    "sure", "surface", "surge", "surprise",
    "surround", "survey", "suspect", "sustain",
    "swallow", "swamp", "swap", "swarm",
    "swear", "sweet", "swift", "swim",
    "swing", "switch", "sword", "symbol",
    "symptom", "syrup", "system", "table",
    "tackle", "tag", "tail", "talent",
    "talk", "tank", "tape", "target",
    "task", "taste", "tattoo", "taxi",
    "teach", "team", "tell", "ten",
    "tenant", "tennis", "tent", "term",
    "test", "text", "thank", "that",
    "theme", "then", "theory", "there",
    "they", "thing", "this", "thought",
    "three", "thrive", "throw", "thumb",
    "thunder", "ticket", "tide", "tiger",
    "tilt", "timber", "time", "tiny",
    "tip", "tired", "tissue", "title",
    "toast", "tobacco", "today", "toddler",
    "toe", "together", "toilet", "token",
    "tomato", "tomorrow", "tone", "tongue",
    "tonight", "tool", "tooth", "top",
    "topic", "topple", "torch", "tornado",
    "tortoise", "toss", "total", "tourist",
    "toward", "tower", "town", "toy",
    "track", "trade", "traffic", "tragic",
    "train", "transfer", "trap", "trash",
    "travel", "tray", "treat", "tree",
    "trend", "trial", "tribe", "trick",
    "trigger", "trim", "trip", "trophy",
    "trouble", "truck", "true", "truly",
    "trumpet", "trust", "truth", "try",
    "tube", "tuition", "tumble", "tuna",
    "tunnel", "turkey", "turn", "turtle",
    "twelve", "twenty", "twice", "twin",
    "twist", "two", "type", "typical",
    "ugly", "umbrella", "unable", "unaware",
    "uncle", "uncover", "under", "undo",
    "unfair", "unfold", "unhappy", "uniform",
    "unique", "unit", "universe", "unknown",
    "unlock", "until", "unusual", "unveil",
    "update", "upgrade", "uphold", "upon",
    "upper", "upset", "urban", "urge",
    "usage", "use", "used", "useful",
    "useless", "usual", "utility", "vacant",
    "vacuum", "vague", "valid", "valley",
    "valve", "van", "vanish", "vapor",
    "various", "vast", "vault", "vehicle",
    "velvet", "vendor", "venture", "venue",
    "verb", "verify", "version", "very",
    "vessel", "veteran", "viable", "vibrant",
    "vicious", "victory", "video", "view",
    "village", "vintage", "violin", "virtual",
    "virus", "visa", "visit", "visual",
    "vital", "vivid", "vocal", "voice",
    "void", "volcano", "volume", "vote",
    "voyage", "wage", "wagon", "wait",
    "walk", "wall", "walnut", "want",
    "warfare", "warm", "warrior", "wash",
    "wasp", "waste", "water", "wave",
    "way", "wealth", "weapon", "wear",
    "weasel", "weather", "web", "wedding",
    "weekend", "weird", "welcome", "west",
    "wet", "whale", "what", "wheat",
    "wheel", "when", "where", "whip",
    "whisper", "wide", "width", "wife",
    "wild", "will", "win", "window",
    "wine", "wing", "wink", "winner",
    "winter", "wire", "wisdom", "wise",
    "wish", "witness", "wolf", "woman",
    "wonder", "wood", "wool", "word",
    "work", "world", "worry", "worth",
    "wrap", "wreck", "wrestle", "wrist",
    "write", "wrong", "yard", "year",
    "yellow", "you", "young", "youth",
    "zebra", "zero", "zone", "zoo",
};

/* ─── PBKDF2-HMAC-SHA512 (RFC 8018) ─────────────────────────────────────────
 * libsodium exposes no PBKDF2 (only scrypt/argon2), so we build it on top of
 * its streaming HMAC-SHA512. The streaming init() takes an explicit key length
 * and performs RFC-2104 key processing, so a password of any length is handled
 * correctly — the same reason src/security.c uses the streaming HMAC-SHA256.  */

#define HMAC_SHA512_LEN crypto_auth_hmacsha512_BYTES /* 64 */

/* HMAC-SHA512(key[key_len], data[data_len]) -> out[64]. Returns true on ok. */
static bool hmac_sha512(const uint8_t *key, size_t key_len,
                        const uint8_t *data, size_t data_len,
                        uint8_t out[HMAC_SHA512_LEN]) {
    crypto_auth_hmacsha512_state st;
    if (crypto_auth_hmacsha512_init(&st, key, key_len) != 0 ||
        crypto_auth_hmacsha512_update(
            &st, data ? data : (const unsigned char *)"", data_len) != 0 ||
        crypto_auth_hmacsha512_final(&st, out) != 0) {
        sodium_memzero(&st, sizeof st);
        return false;
    }
    sodium_memzero(&st, sizeof st);
    return true;
}

/*
 * PBKDF2-HMAC-SHA512(password, salt, iterations) -> out[out_len].
 * Straight RFC 8018 §5.2. dk_len here is always 64 (a single SHA-512 block),
 * so there is exactly one output block T_1 and no block-index looping is
 * strictly required — but the general loop is kept for clarity/robustness.
 */
static bool pbkdf2_hmac_sha512(const uint8_t *password, size_t password_len,
                               const uint8_t *salt, size_t salt_len,
                               uint32_t iterations,
                               uint8_t *out, size_t out_len) {
    const size_t hlen = HMAC_SHA512_LEN;
    /* Number of hLen-sized output blocks. */
    size_t blocks = (out_len + hlen - 1) / hlen;
    uint8_t u[HMAC_SHA512_LEN];
    uint8_t t[HMAC_SHA512_LEN];
    /* Salt || INT(i) buffer for the first HMAC of each block. */
    uint8_t *salt_block = (uint8_t *)malloc(salt_len + 4);
    if (!salt_block) return false;
    if (salt_len && salt) memcpy(salt_block, salt, salt_len);

    bool ok = true;
    for (size_t i = 1; i <= blocks && ok; i++) {
        /* U_1 = HMAC(password, salt || INT_BE_32(i)) */
        salt_block[salt_len + 0] = (uint8_t)((i >> 24) & 0xFF);
        salt_block[salt_len + 1] = (uint8_t)((i >> 16) & 0xFF);
        salt_block[salt_len + 2] = (uint8_t)((i >> 8) & 0xFF);
        salt_block[salt_len + 3] = (uint8_t)(i & 0xFF);

        if (!hmac_sha512(password, password_len, salt_block, salt_len + 4, u)) {
            ok = false;
            break;
        }
        memcpy(t, u, hlen); /* T_i = U_1 */

        for (uint32_t c = 1; c < iterations; c++) {
            /* U_{c+1} = HMAC(password, U_c); T_i ^= U_{c+1} */
            if (!hmac_sha512(password, password_len, u, hlen, u)) {
                ok = false;
                break;
            }
            for (size_t j = 0; j < hlen; j++) t[j] ^= u[j];
        }
        if (!ok) break;

        /* Copy this block into the output (truncating the final block). */
        size_t offset = (i - 1) * hlen;
        size_t take = (out_len - offset < hlen) ? (out_len - offset) : hlen;
        memcpy(out + offset, t, take);
    }

    sodium_memzero(u, sizeof u);
    sodium_memzero(t, sizeof t);
    sodium_memzero(salt_block, salt_len + 4);
    free(salt_block);
    return ok;
}

/* ─── Word split / lookup helpers ───────────────────────────────────────────
 * Whitespace handling mirrors the C# SplitWords():
 * String.Split(null, RemoveEmptyEntries) collapses any run of whitespace and
 * drops empty tokens. For the ASCII BIP-39 domain a single space is the norm;
 * we treat any ASCII whitespace as a separator so a phrase with stray spaces
 * still parses to the same words the C# side would produce.                  */

static int is_ws(char c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' ||
           c == '\v' || c == '\f';
}

/*
 * Binary search for `word[len]` in the (lexicographically sorted) wordlist.
 * The official English list is sorted, so this is O(log n) and needs no
 * precomputed hash map (the C# side builds a Dictionary; the result is
 * identical). Returns the 0..2047 index, or -1 if not found.
 */
static int wordlist_index(const char *word, size_t len) {
    int lo = 0, hi = (int)AETHERNET_BIP39_WORDLIST_SIZE - 1;
    while (lo <= hi) {
        int mid = lo + (hi - lo) / 2;
        const char *w = aethernet_bip39_wordlist[mid];
        /* Compare exactly len bytes, then require w to also end at len. */
        int cmp = strncmp(word, w, len);
        if (cmp == 0) cmp = (w[len] == '\0') ? 0 : -1;
        if (cmp == 0) return mid;
        if (cmp < 0) hi = mid - 1;
        else lo = mid + 1;
    }
    return -1;
}

/*
 * Tokenize `mnemonic` into up to `max_words` (start,len) word slices.
 * Returns the number of words, or -1 if there are more than max_words.
 */
static int split_words(const char *mnemonic,
                       const char **starts, size_t *lens, int max_words) {
    int n = 0;
    const char *p = mnemonic;
    while (*p) {
        while (*p && is_ws(*p)) p++;
        if (!*p) break;
        const char *start = p;
        while (*p && !is_ws(*p)) p++;
        if (n >= max_words) return -1;
        starts[n] = start;
        lens[n] = (size_t)(p - start);
        n++;
    }
    return n;
}

/* ─── Public API ────────────────────────────────────────────────────────── */

bool aethernet_bip39_entropy_to_mnemonic(const uint8_t *entropy,
                                          size_t entropy_len,
                                          char *out,
                                          size_t out_cap) {
    if (!entropy || !out) return false;
    if (entropy_len < 16 || entropy_len > 32 || (entropy_len % 4) != 0)
        return false;

    const size_t ent_bits = entropy_len * 8;
    const size_t cs_bits = ent_bits / 32;                 /* 4..8 */
    const size_t word_count = (ent_bits + cs_bits) / 11;  /* 12..24 */

    /* checksum = first byte of SHA-256(entropy); only the top cs_bits used. */
    uint8_t hash[crypto_hash_sha256_BYTES];
    if (crypto_hash_sha256(hash, entropy, entropy_len) != 0) return false;
    const uint8_t checksum = hash[0];

    size_t pos = 0;
    for (size_t w = 0; w < word_count; w++) {
        /* Read one big-endian 11-bit group from entropy||checksum. */
        int index = 0;
        for (int b = 0; b < 11; b++) {
            size_t bit_pos = w * 11 + (size_t)b;
            int bit;
            if (bit_pos < ent_bits) {
                bit = (entropy[bit_pos >> 3] >> (7 - (bit_pos & 7))) & 1;
            } else {
                bit = (checksum >> (7 - (int)(bit_pos - ent_bits))) & 1;
            }
            index = (index << 1) | bit;
        }

        const char *word = aethernet_bip39_wordlist[index];
        size_t wl = strlen(word);
        /* Need a leading space for every word after the first. */
        size_t need = wl + (w > 0 ? 1 : 0);
        if (pos + need + 1 > out_cap) { /* +1 for NUL */
            sodium_memzero(hash, sizeof hash);
            return false;
        }
        if (w > 0) out[pos++] = ' ';
        memcpy(out + pos, word, wl);
        pos += wl;
    }
    out[pos] = '\0';
    sodium_memzero(hash, sizeof hash);
    return true;
}

bool aethernet_bip39_mnemonic_to_entropy(const char *mnemonic,
                                          uint8_t *out_entropy,
                                          size_t out_cap,
                                          size_t *out_len) {
    if (!mnemonic || !out_entropy) return false;

    const char *starts[24];
    size_t lens[24];
    int word_count = split_words(mnemonic, starts, lens, 24);
    if (word_count != 12 && word_count != 15 && word_count != 18 &&
        word_count != 21 && word_count != 24) {
        return false;
    }

    const size_t total_bits = (size_t)word_count * 11;
    const size_t cs_bits = total_bits / 33;      /* 4..8 */
    const size_t ent_bits = total_bits - cs_bits;
    const size_t ent_bytes = ent_bits / 8;       /* 16..32 */
    if (ent_bytes > out_cap) return false;

    uint8_t entropy[32];
    memset(entropy, 0, sizeof entropy);
    int actual_checksum = 0;

    for (int w = 0; w < word_count; w++) {
        int index = wordlist_index(starts[w], lens[w]);
        if (index < 0) return false; /* unknown word */

        for (int b = 0; b < 11; b++) {
            int bit = (index >> (10 - b)) & 1;
            size_t bit_pos = (size_t)w * 11 + (size_t)b;
            if (bit_pos < ent_bits) {
                entropy[bit_pos >> 3] |= (uint8_t)(bit << (7 - (bit_pos & 7)));
            } else {
                actual_checksum = (actual_checksum << 1) | bit;
            }
        }
    }

    uint8_t hash[crypto_hash_sha256_BYTES];
    if (crypto_hash_sha256(hash, entropy, ent_bytes) != 0) {
        sodium_memzero(entropy, sizeof entropy);
        return false;
    }
    int expected_checksum = hash[0] >> (8 - (int)cs_bits);
    sodium_memzero(hash, sizeof hash);

    if (actual_checksum != expected_checksum) {
        sodium_memzero(entropy, sizeof entropy);
        return false; /* checksum mismatch — reject */
    }

    memcpy(out_entropy, entropy, ent_bytes);
    if (out_len) *out_len = ent_bytes;
    sodium_memzero(entropy, sizeof entropy);
    return true;
}

bool aethernet_bip39_mnemonic_to_seed(const char *mnemonic,
                                      const char *passphrase,
                                      uint8_t *out_seed) {
    if (!mnemonic || !out_seed) return false;
    if (!passphrase) passphrase = "";

    /* Canonicalize the mnemonic to single-space-separated words, matching the
     * C# reference (string.Join(' ', SplitWords(...))). NFKD of ASCII is the
     * identity, so the resulting UTF-8 bytes are the PBKDF2 password.         */
    const char *starts[24];
    size_t lens[24];
    int word_count = split_words(mnemonic, starts, lens, 24);
    if (word_count < 0) return false; /* absurdly long input; reject */

    /* Build the normalized phrase. Longest word 8 chars; 24 words + 23 spaces
     * fits comfortably, but size it from the actual tokens to be safe.        */
    size_t phrase_len = 0;
    for (int i = 0; i < word_count; i++)
        phrase_len += lens[i] + (i > 0 ? 1 : 0);
    uint8_t *phrase = (uint8_t *)malloc(phrase_len ? phrase_len : 1);
    if (!phrase) return false;
    {
        size_t o = 0;
        for (int i = 0; i < word_count; i++) {
            if (i > 0) phrase[o++] = ' ';
            memcpy(phrase + o, starts[i], lens[i]);
            o += lens[i];
        }
    }

    /* salt = "mnemonic" + passphrase (ASCII/UTF-8 bytes). */
    const char *prefix = "mnemonic";
    size_t prefix_len = strlen(prefix);
    size_t pass_len = strlen(passphrase);
    size_t salt_len = prefix_len + pass_len;
    uint8_t *salt = (uint8_t *)malloc(salt_len ? salt_len : 1);
    if (!salt) {
        sodium_memzero(phrase, phrase_len ? phrase_len : 1);
        free(phrase);
        return false;
    }
    memcpy(salt, prefix, prefix_len);
    memcpy(salt + prefix_len, passphrase, pass_len);

    bool ok = pbkdf2_hmac_sha512(phrase, phrase_len, salt, salt_len,
                                 AETHERNET_BIP39_PBKDF2_ITERATIONS,
                                 out_seed, AETHERNET_BIP39_SEED_SIZE);

    sodium_memzero(phrase, phrase_len ? phrase_len : 1);
    sodium_memzero(salt, salt_len ? salt_len : 1);
    free(phrase);
    free(salt);
    return ok;
}

bool aethernet_bip39_is_valid(const char *mnemonic) {
    uint8_t entropy[32];
    size_t len = 0;
    return aethernet_bip39_mnemonic_to_entropy(mnemonic, entropy,
                                               sizeof entropy, &len);
}

/* ─── Identity backup ───────────────────────────────────────────────────── */

bool aethernet_identity_to_recovery_phrase(const uint8_t *ed25519_private_key,
                                            char *out,
                                            size_t out_cap) {
    if (!ed25519_private_key || !out) return false;
    /* An identity private key must be exactly a 32-byte (256-bit) seed. */
    return aethernet_bip39_entropy_to_mnemonic(
        ed25519_private_key, AETHERNET_BIP39_IDENTITY_SEED_SIZE, out, out_cap);
}

bool aethernet_identity_from_recovery_phrase(const char *recovery_phrase,
                                             uint8_t *out_private_key,
                                             uint8_t *out_public_key) {
    if (!recovery_phrase || !out_private_key || !out_public_key) return false;

    uint8_t entropy[32];
    size_t ent_len = 0;
    if (!aethernet_bip39_mnemonic_to_entropy(recovery_phrase, entropy,
                                             sizeof entropy, &ent_len)) {
        return false; /* malformed / bad checksum */
    }
    /* Only a 24-word (256-bit) phrase encodes an AetherNet identity seed. */
    if (ent_len != AETHERNET_BIP39_IDENTITY_SEED_SIZE) {
        sodium_memzero(entropy, sizeof entropy);
        return false;
    }

    /* Derive the Ed25519 public key from the 32-byte seed, exactly as
     * Ed25519SigningService.DerivePublicKey does (libsodium seed_keypair).    */
    unsigned char pk[crypto_sign_ed25519_PUBLICKEYBYTES]; /* 32 */
    unsigned char sk[crypto_sign_ed25519_SECRETKEYBYTES]; /* 64 */
    if (sodium_init() < 0) {
        sodium_memzero(entropy, sizeof entropy);
        return false;
    }
    if (crypto_sign_ed25519_seed_keypair(pk, sk, entropy) != 0) {
        sodium_memzero(entropy, sizeof entropy);
        sodium_memzero(sk, sizeof sk);
        return false;
    }

    memcpy(out_private_key, entropy, AETHERNET_BIP39_IDENTITY_SEED_SIZE);
    memcpy(out_public_key, pk, AETHERNET_BIP39_ED25519_PUBLIC_KEY_SIZE);

    sodium_memzero(entropy, sizeof entropy);
    sodium_memzero(sk, sizeof sk);
    return true;
}
