// SPDX-License-Identifier: MIT

package security

import (
	"crypto/ed25519"
	"crypto/sha256"
	"fmt"
	"strings"

	"golang.org/x/crypto/pbkdf2"

	"crypto/sha512"
)

// BIP-39 recovery-phrase backup for an AetherNet identity.
//
// An AetherNet identity is an Ed25519 key pair whose private key is a 32-byte
// seed — exactly 256 bits, which map cleanly onto a 24-word BIP-39 phrase. The
// user writes down 24 ordinary words; from those words alone the identity is
// fully reconstructed on any device, fully offline. No server, no account, no
// custodian holds anything — the phrase *is* the identity.
//
// This is the real, standard BIP-39 algorithm, verified against the official
// Trezor test vectors (fixtures/bip39/vectors.json). A phrase produced here
// restores on any conformant BIP-39 wallet, and every AetherNet language SDK
// (C#, Go, ...) reproduces the same words and seed byte-for-byte.
//
//	entropy (16..32 bytes, multiple of 4)  --EntropyToMnemonic-->  phrase
//	phrase  --MnemonicToEntropy-->  entropy      (SHA-256 checksum enforced)
//	phrase  --MnemonicToSeed-->  64-byte seed     (PBKDF2-HMAC-SHA512, 2048 rounds)
const (
	bip39PbkdfIterations = 2048
	bip39SeedLengthBytes = 64
)

// bip39WordIndex maps each word to its 0..2047 index, built once from the
// embedded official wordlist.
var bip39WordIndex = buildBip39Index()

func buildBip39Index() map[string]int {
	m := make(map[string]int, len(bip39Words))
	for i, w := range bip39Words {
		m[w] = i
	}
	return m
}

// EntropyToMnemonic encodes entropy as a BIP-39 mnemonic phrase
// (single-space-separated words). Entropy must be 16, 20, 24, 28, or 32 bytes
// (128..256 bits).
func EntropyToMnemonic(entropy []byte) (string, error) {
	if len(entropy) < 16 || len(entropy) > 32 || len(entropy)%4 != 0 {
		return "", fmt.Errorf("entropy must be 16, 20, 24, 28, or 32 bytes, got %d", len(entropy))
	}

	entBits := len(entropy) * 8
	csBits := entBits / 32 // 4..8 checksum bits
	sum := sha256.Sum256(entropy)
	checksum := sum[0] // only the top csBits are used

	// Read the big-endian bit stream entropy||checksum in 11-bit groups.
	wordCount := (entBits + csBits) / 11
	words := make([]string, wordCount)

	for w := 0; w < wordCount; w++ {
		index := 0
		for b := 0; b < 11; b++ {
			bitPos := w*11 + b
			var bit int
			if bitPos < entBits {
				bit = int(entropy[bitPos>>3]>>(7-(bitPos&7))) & 1
			} else {
				bit = int(checksum>>(7-(bitPos-entBits))) & 1
			}
			index = (index << 1) | bit
		}
		words[w] = bip39Words[index]
	}

	return strings.Join(words, " "), nil
}

// MnemonicToEntropy decodes a BIP-39 mnemonic back to its entropy, enforcing the
// SHA-256 checksum. Returns an error on an unknown word, a wrong word count, or a
// checksum mismatch — so a mistyped phrase is rejected rather than silently
// yielding the wrong secret.
func MnemonicToEntropy(mnemonic string) ([]byte, error) {
	words := splitBip39Words(mnemonic)
	switch len(words) {
	case 12, 15, 18, 21, 24:
		// ok
	default:
		return nil, fmt.Errorf("mnemonic must be 12, 15, 18, 21, or 24 words, got %d", len(words))
	}

	totalBits := len(words) * 11
	csBits := totalBits / 33
	entBits := totalBits - csBits
	entropy := make([]byte, entBits/8)
	actualChecksum := 0

	for w := 0; w < len(words); w++ {
		index, ok := bip39WordIndex[words[w]]
		if !ok {
			return nil, fmt.Errorf("unknown mnemonic word: %q", words[w])
		}

		for b := 0; b < 11; b++ {
			bit := (index >> (10 - b)) & 1
			bitPos := w*11 + b
			if bitPos < entBits {
				entropy[bitPos>>3] |= byte(bit << (7 - (bitPos & 7)))
			} else {
				actualChecksum = (actualChecksum << 1) | bit
			}
		}
	}

	sum := sha256.Sum256(entropy)
	expectedChecksum := int(sum[0]) >> (8 - csBits)
	if actualChecksum != expectedChecksum {
		return nil, fmt.Errorf("mnemonic checksum is invalid")
	}

	return entropy, nil
}

// MnemonicToSeed derives the 64-byte BIP-39 seed from a mnemonic and optional
// passphrase, using PBKDF2-HMAC-SHA512 with 2048 iterations and salt
// "mnemonic"+passphrase.
//
// Per the spec both inputs are NFKD-normalized. The official English wordlist is
// entirely ASCII and therefore NFKD-invariant, as is the "mnemonic" salt prefix;
// this repo intentionally does not pull in golang.org/x/text, so the inputs are
// used as-is. A non-ASCII passphrase would need NFKD normalization to match other
// wallets byte-for-byte — documented here rather than silently unhandled.
func MnemonicToSeed(mnemonic, passphrase string) []byte {
	normalizedMnemonic := strings.Join(splitBip39Words(mnemonic), " ")
	salt := "mnemonic" + passphrase

	return pbkdf2.Key(
		[]byte(normalizedMnemonic),
		[]byte(salt),
		bip39PbkdfIterations,
		bip39SeedLengthBytes,
		sha512.New,
	)
}

// IsValidMnemonic reports whether mnemonic is a well-formed BIP-39 phrase with a
// valid checksum.
func IsValidMnemonic(mnemonic string) bool {
	_, err := MnemonicToEntropy(mnemonic)
	return err == nil
}

// ToRecoveryPhrase produces the 24-word recovery phrase for an identity's 32-byte
// Ed25519 private seed (as returned by Ed25519Service.GenerateKeyPair).
func ToRecoveryPhrase(ed25519PrivateKey []byte) (string, error) {
	if len(ed25519PrivateKey) != 32 {
		return "", fmt.Errorf("an AetherNet identity private key must be 32 bytes, got %d", len(ed25519PrivateKey))
	}
	return EntropyToMnemonic(ed25519PrivateKey)
}

// FromRecoveryPhrase restores a full identity key pair from a 24-word recovery
// phrase. The BIP-39 checksum is enforced, so a mistyped word is rejected rather
// than silently reconstructing a different identity. The 32-byte private seed is
// the phrase's entropy; the public key is derived from it.
//
// Returns (publicKey 32 bytes, privateKey/seed 32 bytes, error).
func FromRecoveryPhrase(recoveryPhrase string) (publicKey, privateKey []byte, err error) {
	privateKey, err = MnemonicToEntropy(recoveryPhrase)
	if err != nil {
		return nil, nil, err
	}
	if len(privateKey) != 32 {
		return nil, nil, fmt.Errorf("an AetherNet recovery phrase must be 24 words (a 256-bit identity seed)")
	}

	// Ed25519 public key from the 32-byte seed. ed25519.NewKeyFromSeed returns the
	// 64-byte expanded private key whose trailing 32 bytes are the public key —
	// matching Ed25519Service and the C# DerivePublicKey reference.
	full := ed25519.NewKeyFromSeed(privateKey)
	publicKey = append([]byte(nil), full[32:]...)
	return publicKey, privateKey, nil
}

// splitBip39Words splits on any run of whitespace, dropping empty fields —
// mirroring the C# StringSplitOptions.RemoveEmptyEntries behaviour so leading,
// trailing, and repeated separators are ignored.
func splitBip39Words(mnemonic string) []string {
	return strings.Fields(mnemonic)
}

// bip39Words is the official BIP-39 English wordlist (2048 words), indexed
// 0..2047. Source: https://github.com/bitcoin/bips/blob/master/bip-0039/english.txt
// SHA-256: 2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda
// Embedded verbatim from fixtures/bip39/english.txt so a recovery phrase generated
// by any AetherNet SDK (or any standard BIP-39 wallet) restores identically.
var bip39Words = []string{
	"abandon", "ability", "able", "about", "above", "absent", "absorb", "abstract",
	"absurd", "abuse", "access", "accident", "account", "accuse", "achieve", "acid",
	"acoustic", "acquire", "across", "act", "action", "actor", "actress", "actual",
	"adapt", "add", "addict", "address", "adjust", "admit", "adult", "advance",
	"advice", "aerobic", "affair", "afford", "afraid", "again", "age", "agent",
	"agree", "ahead", "aim", "air", "airport", "aisle", "alarm", "album",
	"alcohol", "alert", "alien", "all", "alley", "allow", "almost", "alone",
	"alpha", "already", "also", "alter", "always", "amateur", "amazing", "among",
	"amount", "amused", "analyst", "anchor", "ancient", "anger", "angle", "angry",
	"animal", "ankle", "announce", "annual", "another", "answer", "antenna", "antique",
	"anxiety", "any", "apart", "apology", "appear", "apple", "approve", "april",
	"arch", "arctic", "area", "arena", "argue", "arm", "armed", "armor",
	"army", "around", "arrange", "arrest", "arrive", "arrow", "art", "artefact",
	"artist", "artwork", "ask", "aspect", "assault", "asset", "assist", "assume",
	"asthma", "athlete", "atom", "attack", "attend", "attitude", "attract", "auction",
	"audit", "august", "aunt", "author", "auto", "autumn", "average", "avocado",
	"avoid", "awake", "aware", "away", "awesome", "awful", "awkward", "axis",
	"baby", "bachelor", "bacon", "badge", "bag", "balance", "balcony", "ball",
	"bamboo", "banana", "banner", "bar", "barely", "bargain", "barrel", "base",
	"basic", "basket", "battle", "beach", "bean", "beauty", "because", "become",
	"beef", "before", "begin", "behave", "behind", "believe", "below", "belt",
	"bench", "benefit", "best", "betray", "better", "between", "beyond", "bicycle",
	"bid", "bike", "bind", "biology", "bird", "birth", "bitter", "black",
	"blade", "blame", "blanket", "blast", "bleak", "bless", "blind", "blood",
	"blossom", "blouse", "blue", "blur", "blush", "board", "boat", "body",
	"boil", "bomb", "bone", "bonus", "book", "boost", "border", "boring",
	"borrow", "boss", "bottom", "bounce", "box", "boy", "bracket", "brain",
	"brand", "brass", "brave", "bread", "breeze", "brick", "bridge", "brief",
	"bright", "bring", "brisk", "broccoli", "broken", "bronze", "broom", "brother",
	"brown", "brush", "bubble", "buddy", "budget", "buffalo", "build", "bulb",
	"bulk", "bullet", "bundle", "bunker", "burden", "burger", "burst", "bus",
	"business", "busy", "butter", "buyer", "buzz", "cabbage", "cabin", "cable",
	"cactus", "cage", "cake", "call", "calm", "camera", "camp", "can",
	"canal", "cancel", "candy", "cannon", "canoe", "canvas", "canyon", "capable",
	"capital", "captain", "car", "carbon", "card", "cargo", "carpet", "carry",
	"cart", "case", "cash", "casino", "castle", "casual", "cat", "catalog",
	"catch", "category", "cattle", "caught", "cause", "caution", "cave", "ceiling",
	"celery", "cement", "census", "century", "cereal", "certain", "chair", "chalk",
	"champion", "change", "chaos", "chapter", "charge", "chase", "chat", "cheap",
	"check", "cheese", "chef", "cherry", "chest", "chicken", "chief", "child",
	"chimney", "choice", "choose", "chronic", "chuckle", "chunk", "churn", "cigar",
	"cinnamon", "circle", "citizen", "city", "civil", "claim", "clap", "clarify",
	"claw", "clay", "clean", "clerk", "clever", "click", "client", "cliff",
	"climb", "clinic", "clip", "clock", "clog", "close", "cloth", "cloud",
	"clown", "club", "clump", "cluster", "clutch", "coach", "coast", "coconut",
	"code", "coffee", "coil", "coin", "collect", "color", "column", "combine",
	"come", "comfort", "comic", "common", "company", "concert", "conduct", "confirm",
	"congress", "connect", "consider", "control", "convince", "cook", "cool", "copper",
	"copy", "coral", "core", "corn", "correct", "cost", "cotton", "couch",
	"country", "couple", "course", "cousin", "cover", "coyote", "crack", "cradle",
	"craft", "cram", "crane", "crash", "crater", "crawl", "crazy", "cream",
	"credit", "creek", "crew", "cricket", "crime", "crisp", "critic", "crop",
	"cross", "crouch", "crowd", "crucial", "cruel", "cruise", "crumble", "crunch",
	"crush", "cry", "crystal", "cube", "culture", "cup", "cupboard", "curious",
	"current", "curtain", "curve", "cushion", "custom", "cute", "cycle", "dad",
	"damage", "damp", "dance", "danger", "daring", "dash", "daughter", "dawn",
	"day", "deal", "debate", "debris", "decade", "december", "decide", "decline",
	"decorate", "decrease", "deer", "defense", "define", "defy", "degree", "delay",
	"deliver", "demand", "demise", "denial", "dentist", "deny", "depart", "depend",
	"deposit", "depth", "deputy", "derive", "describe", "desert", "design", "desk",
	"despair", "destroy", "detail", "detect", "develop", "device", "devote", "diagram",
	"dial", "diamond", "diary", "dice", "diesel", "diet", "differ", "digital",
	"dignity", "dilemma", "dinner", "dinosaur", "direct", "dirt", "disagree", "discover",
	"disease", "dish", "dismiss", "disorder", "display", "distance", "divert", "divide",
	"divorce", "dizzy", "doctor", "document", "dog", "doll", "dolphin", "domain",
	"donate", "donkey", "donor", "door", "dose", "double", "dove", "draft",
	"dragon", "drama", "drastic", "draw", "dream", "dress", "drift", "drill",
	"drink", "drip", "drive", "drop", "drum", "dry", "duck", "dumb",
	"dune", "during", "dust", "dutch", "duty", "dwarf", "dynamic", "eager",
	"eagle", "early", "earn", "earth", "easily", "east", "easy", "echo",
	"ecology", "economy", "edge", "edit", "educate", "effort", "egg", "eight",
	"either", "elbow", "elder", "electric", "elegant", "element", "elephant", "elevator",
	"elite", "else", "embark", "embody", "embrace", "emerge", "emotion", "employ",
	"empower", "empty", "enable", "enact", "end", "endless", "endorse", "enemy",
	"energy", "enforce", "engage", "engine", "enhance", "enjoy", "enlist", "enough",
	"enrich", "enroll", "ensure", "enter", "entire", "entry", "envelope", "episode",
	"equal", "equip", "era", "erase", "erode", "erosion", "error", "erupt",
	"escape", "essay", "essence", "estate", "eternal", "ethics", "evidence", "evil",
	"evoke", "evolve", "exact", "example", "excess", "exchange", "excite", "exclude",
	"excuse", "execute", "exercise", "exhaust", "exhibit", "exile", "exist", "exit",
	"exotic", "expand", "expect", "expire", "explain", "expose", "express", "extend",
	"extra", "eye", "eyebrow", "fabric", "face", "faculty", "fade", "faint",
	"faith", "fall", "false", "fame", "family", "famous", "fan", "fancy",
	"fantasy", "farm", "fashion", "fat", "fatal", "father", "fatigue", "fault",
	"favorite", "feature", "february", "federal", "fee", "feed", "feel", "female",
	"fence", "festival", "fetch", "fever", "few", "fiber", "fiction", "field",
	"figure", "file", "film", "filter", "final", "find", "fine", "finger",
	"finish", "fire", "firm", "first", "fiscal", "fish", "fit", "fitness",
	"fix", "flag", "flame", "flash", "flat", "flavor", "flee", "flight",
	"flip", "float", "flock", "floor", "flower", "fluid", "flush", "fly",
	"foam", "focus", "fog", "foil", "fold", "follow", "food", "foot",
	"force", "forest", "forget", "fork", "fortune", "forum", "forward", "fossil",
	"foster", "found", "fox", "fragile", "frame", "frequent", "fresh", "friend",
	"fringe", "frog", "front", "frost", "frown", "frozen", "fruit", "fuel",
	"fun", "funny", "furnace", "fury", "future", "gadget", "gain", "galaxy",
	"gallery", "game", "gap", "garage", "garbage", "garden", "garlic", "garment",
	"gas", "gasp", "gate", "gather", "gauge", "gaze", "general", "genius",
	"genre", "gentle", "genuine", "gesture", "ghost", "giant", "gift", "giggle",
	"ginger", "giraffe", "girl", "give", "glad", "glance", "glare", "glass",
	"glide", "glimpse", "globe", "gloom", "glory", "glove", "glow", "glue",
	"goat", "goddess", "gold", "good", "goose", "gorilla", "gospel", "gossip",
	"govern", "gown", "grab", "grace", "grain", "grant", "grape", "grass",
	"gravity", "great", "green", "grid", "grief", "grit", "grocery", "group",
	"grow", "grunt", "guard", "guess", "guide", "guilt", "guitar", "gun",
	"gym", "habit", "hair", "half", "hammer", "hamster", "hand", "happy",
	"harbor", "hard", "harsh", "harvest", "hat", "have", "hawk", "hazard",
	"head", "health", "heart", "heavy", "hedgehog", "height", "hello", "helmet",
	"help", "hen", "hero", "hidden", "high", "hill", "hint", "hip",
	"hire", "history", "hobby", "hockey", "hold", "hole", "holiday", "hollow",
	"home", "honey", "hood", "hope", "horn", "horror", "horse", "hospital",
	"host", "hotel", "hour", "hover", "hub", "huge", "human", "humble",
	"humor", "hundred", "hungry", "hunt", "hurdle", "hurry", "hurt", "husband",
	"hybrid", "ice", "icon", "idea", "identify", "idle", "ignore", "ill",
	"illegal", "illness", "image", "imitate", "immense", "immune", "impact", "impose",
	"improve", "impulse", "inch", "include", "income", "increase", "index", "indicate",
	"indoor", "industry", "infant", "inflict", "inform", "inhale", "inherit", "initial",
	"inject", "injury", "inmate", "inner", "innocent", "input", "inquiry", "insane",
	"insect", "inside", "inspire", "install", "intact", "interest", "into", "invest",
	"invite", "involve", "iron", "island", "isolate", "issue", "item", "ivory",
	"jacket", "jaguar", "jar", "jazz", "jealous", "jeans", "jelly", "jewel",
	"job", "join", "joke", "journey", "joy", "judge", "juice", "jump",
	"jungle", "junior", "junk", "just", "kangaroo", "keen", "keep", "ketchup",
	"key", "kick", "kid", "kidney", "kind", "kingdom", "kiss", "kit",
	"kitchen", "kite", "kitten", "kiwi", "knee", "knife", "knock", "know",
	"lab", "label", "labor", "ladder", "lady", "lake", "lamp", "language",
	"laptop", "large", "later", "latin", "laugh", "laundry", "lava", "law",
	"lawn", "lawsuit", "layer", "lazy", "leader", "leaf", "learn", "leave",
	"lecture", "left", "leg", "legal", "legend", "leisure", "lemon", "lend",
	"length", "lens", "leopard", "lesson", "letter", "level", "liar", "liberty",
	"library", "license", "life", "lift", "light", "like", "limb", "limit",
	"link", "lion", "liquid", "list", "little", "live", "lizard", "load",
	"loan", "lobster", "local", "lock", "logic", "lonely", "long", "loop",
	"lottery", "loud", "lounge", "love", "loyal", "lucky", "luggage", "lumber",
	"lunar", "lunch", "luxury", "lyrics", "machine", "mad", "magic", "magnet",
	"maid", "mail", "main", "major", "make", "mammal", "man", "manage",
	"mandate", "mango", "mansion", "manual", "maple", "marble", "march", "margin",
	"marine", "market", "marriage", "mask", "mass", "master", "match", "material",
	"math", "matrix", "matter", "maximum", "maze", "meadow", "mean", "measure",
	"meat", "mechanic", "medal", "media", "melody", "melt", "member", "memory",
	"mention", "menu", "mercy", "merge", "merit", "merry", "mesh", "message",
	"metal", "method", "middle", "midnight", "milk", "million", "mimic", "mind",
	"minimum", "minor", "minute", "miracle", "mirror", "misery", "miss", "mistake",
	"mix", "mixed", "mixture", "mobile", "model", "modify", "mom", "moment",
	"monitor", "monkey", "monster", "month", "moon", "moral", "more", "morning",
	"mosquito", "mother", "motion", "motor", "mountain", "mouse", "move", "movie",
	"much", "muffin", "mule", "multiply", "muscle", "museum", "mushroom", "music",
	"must", "mutual", "myself", "mystery", "myth", "naive", "name", "napkin",
	"narrow", "nasty", "nation", "nature", "near", "neck", "need", "negative",
	"neglect", "neither", "nephew", "nerve", "nest", "net", "network", "neutral",
	"never", "news", "next", "nice", "night", "noble", "noise", "nominee",
	"noodle", "normal", "north", "nose", "notable", "note", "nothing", "notice",
	"novel", "now", "nuclear", "number", "nurse", "nut", "oak", "obey",
	"object", "oblige", "obscure", "observe", "obtain", "obvious", "occur", "ocean",
	"october", "odor", "off", "offer", "office", "often", "oil", "okay",
	"old", "olive", "olympic", "omit", "once", "one", "onion", "online",
	"only", "open", "opera", "opinion", "oppose", "option", "orange", "orbit",
	"orchard", "order", "ordinary", "organ", "orient", "original", "orphan", "ostrich",
	"other", "outdoor", "outer", "output", "outside", "oval", "oven", "over",
	"own", "owner", "oxygen", "oyster", "ozone", "pact", "paddle", "page",
	"pair", "palace", "palm", "panda", "panel", "panic", "panther", "paper",
	"parade", "parent", "park", "parrot", "party", "pass", "patch", "path",
	"patient", "patrol", "pattern", "pause", "pave", "payment", "peace", "peanut",
	"pear", "peasant", "pelican", "pen", "penalty", "pencil", "people", "pepper",
	"perfect", "permit", "person", "pet", "phone", "photo", "phrase", "physical",
	"piano", "picnic", "picture", "piece", "pig", "pigeon", "pill", "pilot",
	"pink", "pioneer", "pipe", "pistol", "pitch", "pizza", "place", "planet",
	"plastic", "plate", "play", "please", "pledge", "pluck", "plug", "plunge",
	"poem", "poet", "point", "polar", "pole", "police", "pond", "pony",
	"pool", "popular", "portion", "position", "possible", "post", "potato", "pottery",
	"poverty", "powder", "power", "practice", "praise", "predict", "prefer", "prepare",
	"present", "pretty", "prevent", "price", "pride", "primary", "print", "priority",
	"prison", "private", "prize", "problem", "process", "produce", "profit", "program",
	"project", "promote", "proof", "property", "prosper", "protect", "proud", "provide",
	"public", "pudding", "pull", "pulp", "pulse", "pumpkin", "punch", "pupil",
	"puppy", "purchase", "purity", "purpose", "purse", "push", "put", "puzzle",
	"pyramid", "quality", "quantum", "quarter", "question", "quick", "quit", "quiz",
	"quote", "rabbit", "raccoon", "race", "rack", "radar", "radio", "rail",
	"rain", "raise", "rally", "ramp", "ranch", "random", "range", "rapid",
	"rare", "rate", "rather", "raven", "raw", "razor", "ready", "real",
	"reason", "rebel", "rebuild", "recall", "receive", "recipe", "record", "recycle",
	"reduce", "reflect", "reform", "refuse", "region", "regret", "regular", "reject",
	"relax", "release", "relief", "rely", "remain", "remember", "remind", "remove",
	"render", "renew", "rent", "reopen", "repair", "repeat", "replace", "report",
	"require", "rescue", "resemble", "resist", "resource", "response", "result", "retire",
	"retreat", "return", "reunion", "reveal", "review", "reward", "rhythm", "rib",
	"ribbon", "rice", "rich", "ride", "ridge", "rifle", "right", "rigid",
	"ring", "riot", "ripple", "risk", "ritual", "rival", "river", "road",
	"roast", "robot", "robust", "rocket", "romance", "roof", "rookie", "room",
	"rose", "rotate", "rough", "round", "route", "royal", "rubber", "rude",
	"rug", "rule", "run", "runway", "rural", "sad", "saddle", "sadness",
	"safe", "sail", "salad", "salmon", "salon", "salt", "salute", "same",
	"sample", "sand", "satisfy", "satoshi", "sauce", "sausage", "save", "say",
	"scale", "scan", "scare", "scatter", "scene", "scheme", "school", "science",
	"scissors", "scorpion", "scout", "scrap", "screen", "script", "scrub", "sea",
	"search", "season", "seat", "second", "secret", "section", "security", "seed",
	"seek", "segment", "select", "sell", "seminar", "senior", "sense", "sentence",
	"series", "service", "session", "settle", "setup", "seven", "shadow", "shaft",
	"shallow", "share", "shed", "shell", "sheriff", "shield", "shift", "shine",
	"ship", "shiver", "shock", "shoe", "shoot", "shop", "short", "shoulder",
	"shove", "shrimp", "shrug", "shuffle", "shy", "sibling", "sick", "side",
	"siege", "sight", "sign", "silent", "silk", "silly", "silver", "similar",
	"simple", "since", "sing", "siren", "sister", "situate", "six", "size",
	"skate", "sketch", "ski", "skill", "skin", "skirt", "skull", "slab",
	"slam", "sleep", "slender", "slice", "slide", "slight", "slim", "slogan",
	"slot", "slow", "slush", "small", "smart", "smile", "smoke", "smooth",
	"snack", "snake", "snap", "sniff", "snow", "soap", "soccer", "social",
	"sock", "soda", "soft", "solar", "soldier", "solid", "solution", "solve",
	"someone", "song", "soon", "sorry", "sort", "soul", "sound", "soup",
	"source", "south", "space", "spare", "spatial", "spawn", "speak", "special",
	"speed", "spell", "spend", "sphere", "spice", "spider", "spike", "spin",
	"spirit", "split", "spoil", "sponsor", "spoon", "sport", "spot", "spray",
	"spread", "spring", "spy", "square", "squeeze", "squirrel", "stable", "stadium",
	"staff", "stage", "stairs", "stamp", "stand", "start", "state", "stay",
	"steak", "steel", "stem", "step", "stereo", "stick", "still", "sting",
	"stock", "stomach", "stone", "stool", "story", "stove", "strategy", "street",
	"strike", "strong", "struggle", "student", "stuff", "stumble", "style", "subject",
	"submit", "subway", "success", "such", "sudden", "suffer", "sugar", "suggest",
	"suit", "summer", "sun", "sunny", "sunset", "super", "supply", "supreme",
	"sure", "surface", "surge", "surprise", "surround", "survey", "suspect", "sustain",
	"swallow", "swamp", "swap", "swarm", "swear", "sweet", "swift", "swim",
	"swing", "switch", "sword", "symbol", "symptom", "syrup", "system", "table",
	"tackle", "tag", "tail", "talent", "talk", "tank", "tape", "target",
	"task", "taste", "tattoo", "taxi", "teach", "team", "tell", "ten",
	"tenant", "tennis", "tent", "term", "test", "text", "thank", "that",
	"theme", "then", "theory", "there", "they", "thing", "this", "thought",
	"three", "thrive", "throw", "thumb", "thunder", "ticket", "tide", "tiger",
	"tilt", "timber", "time", "tiny", "tip", "tired", "tissue", "title",
	"toast", "tobacco", "today", "toddler", "toe", "together", "toilet", "token",
	"tomato", "tomorrow", "tone", "tongue", "tonight", "tool", "tooth", "top",
	"topic", "topple", "torch", "tornado", "tortoise", "toss", "total", "tourist",
	"toward", "tower", "town", "toy", "track", "trade", "traffic", "tragic",
	"train", "transfer", "trap", "trash", "travel", "tray", "treat", "tree",
	"trend", "trial", "tribe", "trick", "trigger", "trim", "trip", "trophy",
	"trouble", "truck", "true", "truly", "trumpet", "trust", "truth", "try",
	"tube", "tuition", "tumble", "tuna", "tunnel", "turkey", "turn", "turtle",
	"twelve", "twenty", "twice", "twin", "twist", "two", "type", "typical",
	"ugly", "umbrella", "unable", "unaware", "uncle", "uncover", "under", "undo",
	"unfair", "unfold", "unhappy", "uniform", "unique", "unit", "universe", "unknown",
	"unlock", "until", "unusual", "unveil", "update", "upgrade", "uphold", "upon",
	"upper", "upset", "urban", "urge", "usage", "use", "used", "useful",
	"useless", "usual", "utility", "vacant", "vacuum", "vague", "valid", "valley",
	"valve", "van", "vanish", "vapor", "various", "vast", "vault", "vehicle",
	"velvet", "vendor", "venture", "venue", "verb", "verify", "version", "very",
	"vessel", "veteran", "viable", "vibrant", "vicious", "victory", "video", "view",
	"village", "vintage", "violin", "virtual", "virus", "visa", "visit", "visual",
	"vital", "vivid", "vocal", "voice", "void", "volcano", "volume", "vote",
	"voyage", "wage", "wagon", "wait", "walk", "wall", "walnut", "want",
	"warfare", "warm", "warrior", "wash", "wasp", "waste", "water", "wave",
	"way", "wealth", "weapon", "wear", "weasel", "weather", "web", "wedding",
	"weekend", "weird", "welcome", "west", "wet", "whale", "what", "wheat",
	"wheel", "when", "where", "whip", "whisper", "wide", "width", "wife",
	"wild", "will", "win", "window", "wine", "wing", "wink", "winner",
	"winter", "wire", "wisdom", "wise", "wish", "witness", "wolf", "woman",
	"wonder", "wood", "wool", "word", "work", "world", "worry", "worth",
	"wrap", "wreck", "wrestle", "wrist", "write", "wrong", "yard", "year",
	"yellow", "you", "young", "youth", "zebra", "zero", "zone", "zoo",
}
